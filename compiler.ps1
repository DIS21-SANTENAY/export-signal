# Recompile ExportSignal.exe (un seul fichier, sans rien à installer) et le dépose à la racine du projet.
# Demande le SDK .NET 8 et une connexion internet la première fois (téléchargement des bibliothèques).
$ErrorActionPreference = 'Stop'
$projet = $PSScriptRoot
$publication = Join-Path $projet 'bin\publication'

dotnet publish (Join-Path $projet 'ExportSignal.csproj') -c Release -o $publication --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw "La compilation n'a pas abouti." }

Copy-Item (Join-Path $publication 'ExportSignal.exe') (Join-Path $projet 'ExportSignal.exe') -Force
$exe = Get-Item (Join-Path $projet 'ExportSignal.exe')
"{0} ({1:N0} Mo)" -f $exe.FullName, ($exe.Length / 1MB)
