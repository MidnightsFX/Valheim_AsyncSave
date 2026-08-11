param(
    [Parameter(Mandatory)]
    [ValidateSet('Debug','Release')]
    [System.String]$Target,

    [Parameter(Mandatory)]
    [System.String]$TargetPath,

    [Parameter(Mandatory)]
    [System.String]$TargetAssembly,

    [Parameter(Mandatory)]
    [System.String]$ProjectPath
)

# Make sure Get-Location is the script path
Push-Location -Path (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (!(Test-Path "$TargetPath")) { Write-Error -ErrorAction Stop -Message "$TargetPath folder is missing" }

# Plugin name without ".dll"
$name = "$TargetAssembly" -Replace('.dll')

# Debug deployment is handled by the CopyToPlugins / CopyToModManagers targets in the csproj.
if ($Target.Equals("Release")) {
    Write-Host "Packaging for ThunderStore..."
    $Package = "Package"
    $PackagePath = "$ProjectPath\$Package"

    # manifest.json version_number and the ModVersion const are separate literals with nothing
    # linking them. Thunderstore rejects a re-upload of an existing version, so warn early.
    $manifestFile = "$PackagePath\manifest.json"
    $sourceFile = "$ProjectPath\$name.cs"
    if ((Test-Path $manifestFile) -and (Test-Path $sourceFile)) {
        $manifestVersion = (Get-Content $manifestFile -Raw | ConvertFrom-Json).version_number
        $modVersionMatch = [regex]::Match((Get-Content $sourceFile -Raw), 'ModVersion\s*=\s*"([^"]+)"')
        if ($modVersionMatch.Success -and $modVersionMatch.Groups[1].Value -ne $manifestVersion) {
            Write-Warning "Version mismatch: manifest.json is $manifestVersion but ModVersion is $($modVersionMatch.Groups[1].Value)"
        }
    }

    Write-Host "$PackagePath\$TargetAssembly"
    New-Item -Type Directory -Path "$PackagePath\plugins" -Force | Out-Null
    Copy-Item -Path "$TargetPath\$TargetAssembly" -Destination "$PackagePath\plugins\$TargetAssembly" -Force

    # The zip is assembled entry by entry rather than with Compress-Archive or
    # ZipFile::CreateFromDirectory. Both of those run on .NET Framework under Windows
    # PowerShell 5.1 and name entries with the platform separator, producing
    # "plugins\AsyncSave.dll". The zip spec mandates "/", so a spec-compliant unpacker reads
    # that as a single filename, never creates the plugins folder, and the dll never reaches
    # BepInEx\plugins. Writing the names ourselves is the only way to guarantee "/".
    $zipPath = "$TargetPath\$name.zip"
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $root = (Resolve-Path $PackagePath).Path.TrimEnd('\')
    $stream = [System.IO.File]::Open($zipPath, [System.IO.FileMode]::Create)
    try {
        $archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create)
        try {
            Get-ChildItem -Path $root -Recurse -File | ForEach-Object {
                $entryName = $_.FullName.Substring($root.Length + 1).Replace('\', '/')
                [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
                    $archive, $_.FullName, $entryName,
                    [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
            }
        } finally { $archive.Dispose() }
    } finally { $stream.Dispose() }
    Write-Host "Build successful, your zip is ready for upload at $zipPath"
}

# Pop Location
Pop-Location
