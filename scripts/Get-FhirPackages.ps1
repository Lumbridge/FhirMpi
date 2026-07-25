param(
    [Parameter(Mandatory = $true)]
    [string] $Destination
)

$ErrorActionPreference = "Stop"
$resolved = [System.IO.Path]::GetFullPath($Destination)
New-Item -ItemType Directory -Force -Path $resolved | Out-Null

$packages = @(
    @{ Name = "hl7.fhir.r4.core"; Version = "4.0.1" },
    @{ Name = "fhir.r4.ukcore.stu2"; Version = "2.0.2" }
)

foreach ($package in $packages) {
    $archive = Join-Path $resolved "$($package.Name)-$($package.Version).tgz"
    $target = Join-Path $resolved "$($package.Name)-$($package.Version)"
    Invoke-WebRequest `
        -Uri "https://packages.simplifier.net/$($package.Name)/$($package.Version)" `
        -OutFile $archive
    New-Item -ItemType Directory -Force -Path $target | Out-Null
    tar -xzf $archive -C $target
}
