// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Management;
using System.Runtime.Versioning;
using Duplicati.Library.Common.IO;
using Duplicati.Library.Interface;

namespace Duplicati.Library.Snapshots.Windows
{
    public class HyperVGuest : IEquatable<HyperVGuest>
    {
        public string Name { get; }
        public Guid ID { get; }
        public List<string> DataPaths { get; }

        public HyperVGuest(string Name, Guid ID, List<string> DataPaths)
        {
            this.Name = Name;
            this.ID = ID;
            this.DataPaths = DataPaths;
        }

        bool IEquatable<HyperVGuest>.Equals(HyperVGuest other)
        {
            return ID.Equals(other.ID);
        }

        public override int GetHashCode()
        {
            return ID.GetHashCode();
        }

        public override bool Equals(object obj)
        {
            HyperVGuest guest = obj as HyperVGuest;
            if (guest != null)
            {
                return Equals(guest);
            }

            return false;
        }

        public static bool operator ==(HyperVGuest guest1, HyperVGuest guest2)
        {
            if (object.ReferenceEquals(guest1, guest2)) return true;
            if (object.ReferenceEquals(guest1, null)) return false;
            if (object.ReferenceEquals(guest2, null)) return false;

            return guest1.Equals(guest2);
        }

        public static bool operator !=(HyperVGuest guest1, HyperVGuest guest2)
        {
            if (object.ReferenceEquals(guest1, guest2)) return false;
            if (object.ReferenceEquals(guest1, null)) return true;
            if (object.ReferenceEquals(guest2, null)) return true;

            return !guest1.Equals(guest2);
        }
    }

    [SupportedOSPlatform("windows")]
    public class HyperVUtility
    {
        /// <summary>
        /// The tag used for logging
        /// </summary>
        private static readonly string LOGTAG = Logging.Log.LogTagFromType<HyperVUtility>();

        /// <summary>
        /// The System.IO abstraction for the Windows platform
        /// </summary>
        private static readonly ISystemIO IO_WIN = SystemIO.IO_OS;

        private readonly ManagementScope _wmiScope;
        private readonly string _vmIdField;
        private readonly string _wmiHost = "localhost";
        private readonly bool _wmiv2Namespace;
        /// <summary>
        /// The Hyper-V VSS Writer Guid
        /// </summary>
        public static readonly Guid HyperVWriterGuid = new Guid("66841cd4-6ded-4f4b-8f17-fd23f8ddc3de");
        /// <summary>
        /// Hyper-V is supported only on Windows platform
        /// </summary>
        public bool IsHyperVInstalled { get; }
        /// <summary>
        /// Hyper-V writer is supported only on Server version of Windows
        /// </summary>
        public bool IsVSSWriterSupported { get; }

        /// <summary>
        /// Enumerated Hyper-V guests
        /// </summary>
        public List<HyperVGuest> Guests { get; }

        public HyperVUtility()
        {
            Guests = new List<HyperVGuest>();

            if (!OperatingSystem.IsWindows())
            {
                IsHyperVInstalled = false;
                IsVSSWriterSupported = false;
                return;
            }

            //Set the namespace depending off host OS
            _wmiv2Namespace = OperatingSystem.IsWindowsVersionAtLeast(6, 2);

            //Set the scope to use in WMI. V2 for Server 2012 or newer.
            _wmiScope = _wmiv2Namespace
                ? new ManagementScope(string.Format("\\\\{0}\\root\\virtualization\\v2", _wmiHost))
                : new ManagementScope(string.Format("\\\\{0}\\root\\virtualization", _wmiHost));
            //Set the VM ID Selector Field for the WMI Query
            _vmIdField = _wmiv2Namespace ? "VirtualSystemIdentifier" : "SystemName";

            Logging.Log.WriteProfilingMessage(LOGTAG, "WMISelect", "Using WMI provider {0}", _wmiScope.Path);

            IsVSSWriterSupported = new ManagementObjectSearcher("SELECT * FROM Win32_OperatingSystem")
                    .Get().OfType<ManagementObject>()
                    .Select(o => (uint)o.GetPropertyValue("ProductType"))
                    .First() != 1;

            try
            {
                IsHyperVInstalled = new ManagementObjectSearcher(_wmiScope, new ObjectQuery(
                    "SELECT * FROM meta_class")).Get().OfType<ManagementObject>()
                    .Any(o => ((ManagementClass)o).ClassPath.ClassName.StartsWith("Msvm_", StringComparison.Ordinal));
            }
            catch { IsHyperVInstalled = false; }

            if (!IsHyperVInstalled)
                Logging.Log.WriteInformationMessage(LOGTAG, "NoHyperVFound", "Cannot open WMI provider {0}. Hyper-V is probably not installed.", _wmiScope.Path);
        }

        /// <summary>
        /// Query Hyper-V for all Virtual Machines info
        /// </summary>
        /// <param name="bIncludePaths">Specify if returned data should contain VM paths</param>
        /// <param name="provider">The provider to use for VSS</param>
        /// <returns>List of Hyper-V Machines</returns>
        public void QueryHyperVGuestsInfo(WindowsSnapshotProvider provider, bool bIncludePaths = false)
        {
            if (!IsHyperVInstalled)
                return;

            Guests.Clear();
            var wmiQuery = _wmiv2Namespace
                ? "SELECT * FROM Msvm_VirtualSystemSettingData WHERE VirtualSystemType = 'Microsoft:Hyper-V:System:Realized'"
                : "SELECT * FROM Msvm_VirtualSystemSettingData WHERE SettingType = 3";

            if (IsVSSWriterSupported)
            {
                using (var moCollection = new ManagementObjectSearcher(_wmiScope, new ObjectQuery(wmiQuery)).Get())
                {
                    if (bIncludePaths)
                    {

                        foreach (var o in GetAllVMsPathsVSS(provider))
                        {
                            foreach (var mObject in moCollection)
                            {
                                if ((string)mObject[_vmIdField] == o.Name)
                                {
                                    Guests.Add(new HyperVGuest((string)mObject["ElementName"], new Guid((string)mObject[_vmIdField]), o.Paths));
                                }
                            }
                        }
                    }
                    else
                    {
                        foreach (var mObject in moCollection)
                            Guests.Add(new HyperVGuest((string)mObject["ElementName"], new Guid((string)mObject[_vmIdField]), null));
                    }
                }
            }
            else
            {
                using (var moCollection = new ManagementObjectSearcher(_wmiScope, new ObjectQuery(wmiQuery)).Get())
                {
                    foreach (var mObject in moCollection)
                    {
                        Guests.Add(new HyperVGuest((string)mObject["ElementName"], new Guid((string)mObject[_vmIdField]), bIncludePaths ?
                            GetVMVhdPathsWMI((string)mObject[_vmIdField])
                                .Union(GetVMConfigPathsWMI((string)mObject[_vmIdField]))
                                .ToList()
                                .ConvertAll(m => m[0].ToString().ToUpperInvariant() + m.Substring(1))
                                .Distinct(Utility.Utility.ClientFilenameStringComparer)
                                .OrderBy(a => a).ToList() : null));
                    }
                }
            }
        }

        /// <summary>
        /// For all Hyper-V guests it enumerate all associated paths using VSS data
        /// </summary>
        /// <returns>A collection of VMs and paths</returns>
        private static IEnumerable<WriterMetaData> GetAllVMsPathsVSS(WindowsSnapshotProvider provider)
        {
            using (var vssBackupComponents = new SnapshotManager(provider))
            {
                var writerGUIDS = new[] { HyperVWriterGuid };

                try
                {
                    vssBackupComponents.SetupWriters(writerGUIDS, null);
                }
                catch (Exception)
                {
                    throw new Interface.UserInformationException("Microsoft Hyper-V VSS Writer not found - cannot backup Hyper-V machines.", "NoHyperVVssWriter");
                }
                foreach (var o in vssBackupComponents.ParseWriterMetaData(writerGUIDS))
                {
                    yield return o;
                }
            }
        }

        /// <summary>
        /// For given Hyper-V guest it enumerate all associated configuration files using WMI data
        /// </summary>
        /// <param name="vmID">ID of VM to get paths for</param>
        /// <returns>A collection of configuration paths</returns>
        private List<string> GetVMConfigPathsWMI(string vmID)
        {
            var result = new List<string>();
            string path;
            var wmiQuery = _wmiv2Namespace
                ? string.Format("select * from Msvm_VirtualSystemSettingData where {0}='{1}'", _vmIdField, vmID)
                : string.Format("select * from Msvm_VirtualSystemGlobalSettingData where {0}='{1}'", _vmIdField, vmID);

            using (var mObject1 = new ManagementObjectSearcher(_wmiScope, new ObjectQuery(wmiQuery)).Get().Cast<ManagementObject>().First())
                if (_wmiv2Namespace)
                {
                    path = IO_WIN.PathCombine((string)mObject1["ConfigurationDataRoot"], (string)mObject1["ConfigurationFile"]);
                    if (File.Exists(path))
                        result.Add(path);

                    using (var snaps = new ManagementObjectSearcher(_wmiScope, new ObjectQuery(string.Format(
                        "SELECT * FROM Msvm_VirtualSystemSettingData where VirtualSystemType='Microsoft:Hyper-V:Snapshot:Realized' and {0}='{1}'",
                        _vmIdField, vmID))).Get())
                    {
                        foreach (var snap in snaps)
                        {
                            path = IO_WIN.PathCombine((string)snap["ConfigurationDataRoot"], (string)snap["ConfigurationFile"]);
                            if (File.Exists(path))
                                result.Add(path);
                            path = Util.AppendDirSeparator(IO_WIN.PathCombine((string)snap["ConfigurationDataRoot"], (string)snap["SuspendDataRoot"]));
                            if (Directory.Exists(path))
                                result.Add(path);
                        }
                    }
                }
                else
                {
                    path = IO_WIN.PathCombine((string)mObject1["ExternalDataRoot"], "Virtual Machines", vmID + ".xml");
                    if (File.Exists(path))
                        result.Add(path);
                    path = Util.AppendDirSeparator(IO_WIN.PathCombine((string)mObject1["ExternalDataRoot"], "Virtual Machines", vmID));
                    if (Directory.Exists(path))
                        result.Add(path);

                    var snapsIDs = new ManagementObjectSearcher(_wmiScope, new ObjectQuery(string.Format(
                        "SELECT * FROM Msvm_VirtualSystemSettingData where SettingType=5 and {0}='{1}'",
                        _vmIdField, vmID))).Get().OfType<ManagementObject>().Select(o => (string)o.GetPropertyValue("InstanceID")).ToList();

                    foreach (var snapID in snapsIDs)
                    {
                        path = IO_WIN.PathCombine((string)mObject1["SnapshotDataRoot"], "Snapshots", snapID.Replace("Microsoft:", "") + ".xml");
                        if (File.Exists(path))
                            result.Add(path);
                        path = Util.AppendDirSeparator(IO_WIN.PathCombine((string)mObject1["SnapshotDataRoot"], "Snapshots", snapID.Replace("Microsoft:", "")));
                        if (Directory.Exists(path))
                            result.Add(path);
                    }
                }

            return result;
        }

        /// <summary>
        /// For given Hyper-V guest it enumerate all associated VHD files using WMI data
        /// </summary>
        /// <param name="vmID">ID of VM to get paths for</param>
        /// <returns>A collection of VHD paths</returns>
        private List<string> GetVMVhdPathsWMI(string vmID)
        {
            var result = new List<string>();
            using (var vm = new ManagementObjectSearcher(_wmiScope, new ObjectQuery(string.Format("select * from Msvm_ComputerSystem where Name = '{0}'", vmID)))
                .Get().OfType<ManagementObject>().First())
            {
                foreach (var sysSettings in vm.GetRelated("MsVM_VirtualSystemSettingData"))
                    using (var systemObjCollection = ((ManagementObject)sysSettings).GetRelated(_wmiv2Namespace ? "MsVM_StorageAllocationSettingData" : "MsVM_ResourceAllocationSettingData"))
                    {
                        List<string> tempvhd;

                        if (_wmiv2Namespace)
                            tempvhd = (from ManagementBaseObject systemBaseObj in systemObjCollection
                                       where ((UInt16)systemBaseObj["ResourceType"] == 31
                                               && (string)systemBaseObj["ResourceSubType"] == "Microsoft:Hyper-V:Virtual Hard Disk")
                                       select ((string[])systemBaseObj["HostResource"])[0]).ToList();
                        else
                            tempvhd = (from ManagementBaseObject systemBaseObj in systemObjCollection
                                       where ((UInt16)systemBaseObj["ResourceType"] == 21
                                               && (string)systemBaseObj["ResourceSubType"] == "Microsoft Virtual Hard Disk")
                                       select ((string[])systemBaseObj["Connection"])[0]).ToList();

                        foreach (var vhd in tempvhd)
                        {
                            if (File.Exists(vhd))
                            {
                                result.Add(vhd);
                            }
                            else
                            {
                                Logging.Log.WriteWarningMessage(LOGTAG, "HyperVInvalidVhd", null, "Invalid VHD file detected, file does not exist: {0}", vhd);
                            }
                        }
                    }
            }

            using (var imgMan = new ManagementObjectSearcher(_wmiScope, new ObjectQuery("select * from MsVM_ImageManagementService")).Get().OfType<ManagementObject>().First())
            {
                var ParentPaths = new List<string>();
                var inParams = imgMan.GetMethodParameters(_wmiv2Namespace ? "GetVirtualHardDiskSettingData" : "GetVirtualHardDiskInfo");

                foreach (var vhdPath in result)
                {
                    inParams["Path"] = vhdPath;
                    using (var outParams = imgMan.InvokeMethod(_wmiv2Namespace ? "GetVirtualHardDiskSettingData" : "GetVirtualHardDiskInfo", inParams, null))
                    {
                        if (outParams != null)
                        {
                            var doc = new System.Xml.XmlDocument();
                            var propertyValue = (string)outParams[_wmiv2Namespace ? "SettingData" : "Info"];

                            if (propertyValue != null)
                            {
                                doc.LoadXml(propertyValue);
                                var node = doc.SelectSingleNode("//PROPERTY[@NAME = 'ParentPath']/VALUE/child::text()");

                                if (node != null && File.Exists(node.Value))
                                    ParentPaths.Add(node.Value);
                            }
                        }
                    }
                }

                result.AddRange(ParentPaths);
            }

            return result;
        }
    }
}