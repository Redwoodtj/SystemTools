﻿using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Streams;
using DiscUtils.Udf;
using DiscUtils.Wim;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using DiscUtilsRegistryHive = DiscUtils.Registry.RegistryHive;
using DiscUtilsRegistryKey = DiscUtils.Registry.RegistryKey;
#if WINDOWS || NETFRAMEWORK
using Microsoft.Win32;
using ROOT.CIMV2;
#endif

namespace GetProductKey
{
    public static class Program
    {
        private static readonly object _syncObj = new();

        static Program()
        {
            var asms = new[]
            {
                typeof(DiscUtils.Fat.FatFileSystem).Assembly,
                typeof(DiscUtils.Ntfs.NtfsFileSystem).Assembly,
                typeof(DiscUtils.Udf.UdfReader).Assembly,
                typeof(DiscUtils.Iso9660.CDReader).Assembly,
                typeof(DiscUtils.Wim.WimFileSystem).Assembly,
                typeof(DiscUtils.Dmg.Disk).Assembly,
                typeof(DiscUtils.Vmdk.Disk).Assembly,
                typeof(DiscUtils.Vdi.Disk).Assembly,
                typeof(DiscUtils.Vhd.Disk).Assembly,
                typeof(DiscUtils.Vhdx.Disk).Assembly
            };
            foreach (var asm in asms.Distinct())
            {
                DiscUtils.Setup.SetupHelper.RegisterAssembly(asm);
            }
        }

        public static void Main(params string[] args)
        {
            if (args != null && args.Length == 1 && args[0].Equals("/?", StringComparison.Ordinal))
            {
                Console.WriteLine("GetProductKey");
                Console.WriteLine("A tool to show Windows installation information including product key.");
                Console.WriteLine("Copyright (c) Olof Lagerkvist, LTR Data, 2021");
                Console.WriteLine("http://ltr-data.se  https://github.com/LTRData");
                Console.WriteLine();
                Console.WriteLine("Syntax to query current machine:");
                Console.WriteLine("GetProductKey");
                Console.WriteLine("Syntax to query another machine on network:");
                Console.WriteLine(@"GetProductKey \\machinename");
                Console.WriteLine(@"Syntax for an offline Windows installation on an attached external harddisk");
                Console.WriteLine(@"GetProductKey D:\");
                Console.WriteLine("Syntax for a virtual machine image (supports vhd, vhdx, vmdk and vdi):");
                Console.WriteLine(@"GetProductKey D:\path\image.vhd");
                Console.WriteLine("Syntax for a setup ISO or WIM image:");
                Console.WriteLine(@"GetProductKey D:\path\windows_setup.iso");

                return;
            }

#if WINDOWS || NETFRAMEWORK
            var online_root_keys = new ConcurrentBag<RegistryKey>();
            Task<string> hardware_prodct_key = null;
#endif
            var offline_root_keys = new ConcurrentBag<DiscUtilsRegistryKey>();
            var value_getters = new ConcurrentBag<KeyValuePair<string, Func<string, object>>>();
            var disposables = new ConcurrentBag<IDisposable>();

            if (args == null || args.Length == 0)
            {
#if WINDOWS || NETFRAMEWORK
                var key = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64).OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                disposables.Add(key);
                online_root_keys.Add(key);
                value_getters.Add(new($@"\\{Environment.MachineName}", key.GetValue));

                hardware_prodct_key = Task.Factory.StartNew(() =>
                {
                    var service = SoftwareLicensingService.GetInstances().OfType<SoftwareLicensingService>().FirstOrDefault();
                    if (service != null)
                    {
                        return $"Hardware product key:    {service.OA3xOriginalProductKey}";
                    }
                    return null;
                });
#else
                Console.ForegroundColor = ConsoleColor.Red;
                Console.Error.WriteLine("Current machine is only supported on Windows.");
                Console.ResetColor();
#endif
            }
            else
            {
                Parallel.ForEach(args, arg =>
                {
                    try
                    {
                        if (arg.StartsWith(@"\\", StringComparison.Ordinal) &&
                            arg.IndexOf('\\', 2) < 0)
                        {
#if WINDOWS || NETFRAMEWORK
                            using var remotehive = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, arg, RegistryView.Registry64);
                            var key = remotehive?.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                            disposables.Add(key);
                            online_root_keys.Add(key);
                            value_getters.Add(new(arg, key.GetValue));
#else
                            throw new PlatformNotSupportedException("Queries to other running machines over network are only supported on Windows.");
#endif
                        }
                        else if (Directory.Exists(arg))
                        {
                            var path = Path.Combine(arg, @"Windows\system32\config\SOFTWARE");
                            var hive = new DiscUtilsRegistryHive(File.OpenRead(path), ownership: Ownership.Dispose);
                            disposables.Add(hive);
                            var key = hive.Root.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion");
                            offline_root_keys.Add(key);
                            value_getters.Add(new(arg, key.GetValue));
                        }
                        else if (File.Exists(arg) && Path.GetExtension(arg).Equals(".iso", StringComparison.OrdinalIgnoreCase))
                        {
                            var file = File.OpenRead(arg);
                            disposables.Add(file);

                            DiscFileSystem iso;
                            if (UdfReader.Detect(file))
                            {
                                iso = new UdfReader(file);
                            }
                            else
                            {
                                iso = new CDReader(file, joliet: true);
                            }
                            
                            var wiminfo = iso.GetFileInfo(@"sources\install.wim");
                            if (!wiminfo.Exists)
                            {
                                wiminfo = iso.GetFileInfo(@"sources\boot.wim");

                                if (!wiminfo.Exists)
                                {
                                    throw new FileNotFoundException(@"Cannot find sources\install.wim in image");
                                }
                            }

                            var image = new WimFile(wiminfo.OpenRead());

                            foreach (var fs in image.EnumerateWimImages())
                            {
                                var hive = new DiscUtilsRegistryHive(fs.Value.OpenRead(), ownership: Ownership.Dispose);
                                var key = hive.Root.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion");
                                offline_root_keys.Add(key);
                                value_getters.Add(new(@$"{arg}\{wiminfo.FullName} index {fs.Key}", name => { lock (file) { return key.GetValue(name); } }));
                            }
                        }
                        else if (File.Exists(arg) && Path.GetExtension(arg).Equals(".wim", StringComparison.OrdinalIgnoreCase))
                        {
                            var file = File.OpenRead(arg);
                            disposables.Add(file);

                            var image = new WimFile(file);

                            foreach (var fs in image.EnumerateWimImages())
                            {
                                var hive = new DiscUtilsRegistryHive(fs.Value.OpenRead(), ownership: Ownership.Dispose);
                                var key = hive.Root.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion");
                                offline_root_keys.Add(key);
                                value_getters.Add(new($"{arg} index {fs.Key}", name => { lock (file) { return key.GetValue(name); } }));
                            }
                        }
                        else if (File.Exists(arg))
                        {
                            var image = VirtualDisk.OpenDisk(arg, FileAccess.Read);

                            if (image == null)
                            {
                                image = new DiscUtils.Raw.Disk(arg, FileAccess.Read);
                            }

                            disposables.Add(image);

                            foreach (var fs in image.EnumerateVirtualDiskImageFileSystems())
                            {
                                var hive = new DiscUtilsRegistryHive(fs.Value.OpenRead(), ownership: Ownership.Dispose);
                                var key = hive.Root.OpenSubKey(@"Microsoft\Windows NT\CurrentVersion");
                                offline_root_keys.Add(key);
                                value_getters.Add(new($"{arg} partition {fs.Key}", name => { lock (image) { return key.GetValue(name); } }));
                            }
                        }
                        else
                        {
                            throw new FileNotFoundException($"File '{arg}' not found");
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (_syncObj)
                        {
                            Console.ForegroundColor = ConsoleColor.Red;
                            Console.Error.WriteLine($"Error opening '{arg}': {ex.GetBaseException().Message}");
                            Console.ResetColor();
                        }
                    }
                });
            }

            Parallel.ForEach(value_getters, obj =>
            {
                try
                {
                    var sb = new StringBuilder()
                    .AppendLine(obj.Key)
                    .AppendLine($"Product name:            {obj.Value("ProductName")}")
                    .AppendLine($"Product Id:              {obj.Value("ProductId")}")
                    .AppendLine($"Edition:                 {obj.Value("EditionID")}")
                    .AppendLine($"Installation type:       {obj.Value("InstallationType")}")
                    .AppendLine($"Version:                 {GetVersion(obj.Value)}")
                    .AppendLine($"Type:                    {obj.Value("CurrentType")}")
                    .AppendLine($"Product key:             {GetProductKey(obj.Value)}")
                    .AppendLine($"Install time (UTC):      {GetInstallTime(obj.Value)}")
                    .AppendLine($"Registered owner:        {obj.Value("RegisteredOwner")}")
                    .AppendLine($"Registered organization: {obj.Value("RegisteredOrganization")}");

#if WINDOWS || NETFRAMEWORK
                    if (hardware_prodct_key != null)
                    {
                        try
                        {
                            var result = hardware_prodct_key.Result;
                            if (result != null)
                            {
                                sb.AppendLine(result);
                            }
                        }
                        catch
                        {
                        }
                    }
#endif

                    var msg = sb.ToString();

                    lock (_syncObj)
                    {
                        Console.WriteLine(msg);
                    }
                }
                catch (Exception ex)
                {
                    lock (_syncObj)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.Error.WriteLine($"Error reading '{obj.Key}': {ex.GetBaseException().Message}");
                        Console.ResetColor();
                    }
                }
            });

            Parallel.ForEach(disposables.OfType<IDisposable>(), obj => obj.Dispose());
        }

        private static string GetVersion(Func<string, object> value)
        {
            var currentMajor = value("CurrentMajorVersionNumber");
            if (currentMajor != null)
            {
                return $"{currentMajor}.{value("CurrentMinorVersionNumber")}.{value("CurrentBuildNumber")} {value("DisplayVersion")}";
            }
            else
            {
                return $"{value("CurrentVersion")}.{value("CurrentBuildNumber")} {value("CSDVersion")} {value("CSDBuildNumber")}";
            }
        }

        private static DateTime? GetInstallTime(Func<string, object> value)
        {
            var time = value("InstallTime") as long?;

            if (time.HasValue)
            {
                return DateTime.FromFileTimeUtc(time.Value);
            }

            var date = value("InstallDate") as int?;

            if (date.HasValue)
            {
                return new DateTime(1970, 1, 1).AddSeconds(date.Value);
            }

            return null;
        }

        public static IEnumerable<KeyValuePair<int, DiscFileInfo>> EnumerateWimImages(this WimFile wim)
        {
            for (var i = 0; i < wim.ImageCount; i++)
            {
                var fs = wim.GetImage(i);
                var hive = fs.GetFileInfo(@"Windows\system32\config\SOFTWARE");

                if (hive != null && hive.Exists)
                {
                    yield return new(i + 1, hive);
                }
            }
        }

        public static IEnumerable<KeyValuePair<int, DiscFileInfo>> EnumerateVirtualDiskImageFileSystems(this VirtualDisk image)
        {
            var partitions = image.Partitions;

            if (partitions != null && partitions.Count > 0)
            {
                for (var i = 0; i < partitions.Count; i++)
                {
                    var partition = partitions[i];

                    var raw = partition.Open();
                    var fsrec = FileSystemManager.DetectFileSystems(raw);

                    DiscFileSystem fs;

                    try
                    {
                        if (fsrec.Length > 0)
                        {
                            fs = fsrec[0].Open(raw);
                        }
                        else
                        {
                            continue;
                        }
                    }
                    catch
                    {
                        continue;
                    }

                    var hive = fs.GetFileInfo(@"Windows\system32\config\SOFTWARE");
                    if (hive == null || !hive.Exists)
                    {
                        hive = fs.GetFileInfo(@"WINNT\system32\config\SOFTWARE");
                    }

                    if (hive != null && hive.Exists)
                    {
                        yield return new(i + 1, hive);
                    }
                }
            }
            else
            {
                var raw = image.Content;
                var fsrec = FileSystemManager.DetectFileSystems(raw);
                if (fsrec.Length > 0)
                {
                    var fs = fsrec[0].Open(raw);

                    var hive = fs.GetFileInfo(@"Windows\system32\config\SOFTWARE");

                    if (hive != null && hive.Exists)
                    {
                        yield return new(0, hive);
                    }
                }
            }
        }

        public static string GetProductKey(Func<string, object> value)
        {
            if (value("DigitalProductId") is not byte[] data || data.Length < 67)
            {
                return null;
            }

            var valueData = data.Skip(52).Take(15).ToArray();
            var productKey = new char[29];
            var o = productKey.Length;
            const string chars = "BCDFGHJKMPQRTVWXY2346789";
            for (var i = 24; i >= 0; i--)
            {
                var r = 0;
                for (var j = 14; j >= 0; j--)
                {
                    r = (r << 8) | valueData[j];
                    valueData[j] = (byte)(r / 24);
                    r %= 24;
                }
                productKey[--o] = chars[r];
                
                if ((i % 5) == 0 && i != 0)
                {
                    productKey[--o] = '-';
                }
            }

            var key = new string(productKey);

            if (key.Equals("BBBBB-BBBBB-BBBBB-BBBBB-BBBBB", StringComparison.Ordinal))
            {
                return null;
            }

            return key;
        }
    }
}