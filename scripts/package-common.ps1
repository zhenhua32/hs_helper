$ErrorActionPreference = 'Stop'

function Assert-NewPackagePath {
    param([string]$Path, [string]$AllowedRoot)
    $taskRoot = [IO.Path]::GetFullPath($AllowedRoot).TrimEnd('\', '/')
    $taskPath = [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
    if (-not $taskPath.StartsWith($taskRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The output must be a new child directory inside the allowed output root.'
    }
    if (Test-Path -LiteralPath $taskPath) { throw 'The output already exists; choose a new directory or version.' }
    $taskAncestor = $taskPath
    while ($taskAncestor) {
        if (Test-Path -LiteralPath $taskAncestor) {
            $taskItem = Get-Item -LiteralPath $taskAncestor -Force
            if (-not $taskItem.PSIsContainer -or ($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Output ancestors must be ordinary directories, without links or junctions.'
            }
        }
        $taskParent = [IO.Path]::GetDirectoryName($taskAncestor)
        if ($taskParent -eq $taskAncestor) { break }
        $taskAncestor = $taskParent
    }
    return $taskPath
}

function Get-CheckedPackageFiles {
    param([string]$Directory)
    $taskBase = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    if (-not (Test-Path -LiteralPath $taskBase -PathType Container) -or $taskBase -eq [IO.Path]::GetPathRoot($taskBase).TrimEnd('\', '/')) {
        throw 'Select an existing package directory, not a filesystem root.'
    }
    $taskAncestor = $taskBase
    while ($taskAncestor) {
        if ((Get-Item -LiteralPath $taskAncestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package ancestors cannot be links or junctions.' }
        $taskAncestor = [IO.Path]::GetDirectoryName($taskAncestor)
    }
    $taskPending = New-Object 'Collections.Generic.Stack[string]'
    $taskPending.Push($taskBase)
    $taskFiles = New-Object 'Collections.Generic.List[object]'
    [long]$taskTotalBytes = 0
    while ($taskPending.Count) {
        $taskCurrent = $taskPending.Pop()
        if ((Get-Item -LiteralPath $taskCurrent -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw 'A package cannot contain or be a link or junction.'
        }
        foreach ($taskItem in Get-ChildItem -LiteralPath $taskCurrent -Force) {
            $taskRelative = $taskItem.FullName.Substring($taskBase.Length + 1).Replace('\', '/')
            if ($taskItem.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Package contains a link: $taskRelative" }
            if ($taskRelative -match '(^|/)(\.git|\.local|data|logs?|cache|obj|bin)(/|$)' -or
                $taskItem.Name -match '(^\.env|\.env$|^model-config\.json$|^appsettings\..*\.Local\.json$|\.(db|sqlite|sqlite3)(-|$)|\.(log|pfx|pem|key)$)') {
                throw "Package contains a prohibited data or credential path: $taskRelative"
            }
            if ($taskItem.PSIsContainer) { $taskPending.Push($taskItem.FullName); continue }
            $taskTotalBytes += $taskItem.Length
            if ($taskFiles.Count -ge 2000 -or $taskTotalBytes -gt 2GB) { throw 'Package exceeds validation bounds (2000 files / 2 GiB).' }
            $taskFiles.Add([pscustomobject]@{ RelativePath = $taskRelative; FullName = $taskItem.FullName; Length = $taskItem.Length })
        }
    }
    return $taskFiles.ToArray() | Sort-Object RelativePath
}

function Assert-PackageRelativePath {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path) -or $Path -match '(^/|\\|:|[\r\n\x00]|(^|/)\.{1,2}(/|$))' -or $Path -match '//') {
        throw 'The manifest contains an invalid relative path.'
    }
}

function Get-EmbeddedAppPolicy {
    param([string]$Executable)
    if (-not ('HsHelper.Packaging.ManifestResource' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
namespace HsHelper.Packaging {
    public static class ManifestResource {
        [DllImport("kernel32.dll", EntryPoint="LoadLibraryExW", CharSet=CharSet.Unicode, SetLastError=true)]
        private static extern IntPtr LoadLibraryEx(string path, IntPtr file, uint flags);
        [DllImport("kernel32.dll", EntryPoint="FindResourceW", SetLastError=true)]
        private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
        [DllImport("kernel32.dll", SetLastError=true)] private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")] private static extern IntPtr LockResource(IntPtr resource);
        [DllImport("kernel32.dll")] private static extern uint SizeofResource(IntPtr module, IntPtr resource);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr module);
        public static string Read(string path) {
            // LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE: inspect PE data, never execute DLL entry points.
            var module = LoadLibraryEx(path, IntPtr.Zero, 0x22);
            if (module == IntPtr.Zero) throw new InvalidDataException("Cannot inspect the executable resource table.");
            try {
                var resource = FindResource(module, new IntPtr(1), new IntPtr(24));
                var size = resource == IntPtr.Zero ? 0 : SizeofResource(module, resource);
                if (size == 0 || size > 1048576) throw new InvalidDataException("Missing or oversized embedded app manifest.");
                var pointer = LockResource(LoadResource(module, resource));
                if (pointer == IntPtr.Zero) throw new InvalidDataException("Cannot read the embedded app manifest.");
                var bytes = new byte[size];
                Marshal.Copy(pointer, bytes, 0, bytes.Length);
                return Encoding.UTF8.GetString(bytes).Trim('\uFEFF', '\0');
            } finally { FreeLibrary(module); }
        }
    }
}
'@
    }
    $taskManifestText = [HsHelper.Packaging.ManifestResource]::Read($Executable)
    $taskXmlSettings = New-Object Xml.XmlReaderSettings
    $taskXmlSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $taskXmlSettings.XmlResolver = $null
    $taskReader = [Xml.XmlReader]::Create((New-Object IO.StringReader($taskManifestText)), $taskXmlSettings)
    $taskDocument = New-Object Xml.XmlDocument
    $taskDocument.XmlResolver = $null
    try { $taskDocument.Load($taskReader) } finally { $taskReader.Dispose() }
    $taskNamespaces = New-Object Xml.XmlNamespaceManager($taskDocument.NameTable)
    $taskNamespaces.AddNamespace('dpi', 'http://schemas.microsoft.com/SMI/2016/WindowsSettings')
    $taskNamespaces.AddNamespace('app', 'urn:schemas-microsoft-com:asm.v3')
    $taskDpi = $taskDocument.SelectSingleNode('//dpi:dpiAwareness', $taskNamespaces)
    $taskPrivilege = $taskDocument.SelectSingleNode('//app:requestedExecutionLevel', $taskNamespaces)
    if (-not $taskDpi -or -not $taskPrivilege) { throw 'The executable manifest lacks DPI or privilege declarations.' }
    return [pscustomobject]@{ DpiAwareness = $taskDpi.InnerText; ExecutionLevel = $taskPrivilege.GetAttribute('level'); UiAccess = $taskPrivilege.GetAttribute('uiAccess') }
}
