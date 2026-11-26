# Dessine l'icône de l'outil (bulle de conversation blanche sur fond bleu DIS21) et l'enregistre en .ico.
# À relancer seulement si l'on veut changer le dessin : source\icone.ico est déjà dans le projet.
Add-Type -AssemblyName System.Drawing

function New-Image([int]$taille) {
    $image = New-Object System.Drawing.Bitmap $taille, $taille
    $g = [System.Drawing.Graphics]::FromImage($image)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $e = $taille / 256.0

    # Fond : carré arrondi bleu.
    $fond = New-Object System.Drawing.Drawing2D.GraphicsPath
    $r = 52 * $e; $c = 256 * $e
    $fond.AddArc(0, 0, 2 * $r, 2 * $r, 180, 90)
    $fond.AddArc($c - 2 * $r, 0, 2 * $r, 2 * $r, 270, 90)
    $fond.AddArc($c - 2 * $r, $c - 2 * $r, 2 * $r, 2 * $r, 0, 90)
    $fond.AddArc(0, $c - 2 * $r, 2 * $r, 2 * $r, 90, 90)
    $fond.CloseFigure()
    $bleu = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x32, 0x71, 0xb7))
    $g.FillPath($bleu, $fond)

    # Bulle blanche avec sa pointe.
    $blanc = [System.Drawing.Brushes]::White
    $g.FillEllipse($blanc, 44 * $e, 46 * $e, 168 * $e, 140 * $e)
    $pointe = @(
        (New-Object System.Drawing.PointF (72 * $e), (150 * $e)),
        (New-Object System.Drawing.PointF (56 * $e), (212 * $e)),
        (New-Object System.Drawing.PointF (120 * $e), (176 * $e))
    )
    $g.FillPolygon($blanc, [System.Drawing.PointF[]]$pointe)

    # Trois traits figurent le texte de la conversation.
    $marine = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(0x18, 0x36, 0x5e))
    foreach ($ligne in @(@(84, 88, 88), @(84, 112, 88), @(84, 136, 56))) {
        $g.FillRectangle($marine, $ligne[0] * $e, $ligne[1] * $e, $ligne[2] * $e, 12 * $e)
    }
    $g.Dispose()
    return $image
}

$tailles = 16, 24, 32, 48, 64, 256
$images = foreach ($t in $tailles) {
    $flux = New-Object System.IO.MemoryStream
    (New-Image $t).Save($flux, [System.Drawing.Imaging.ImageFormat]::Png)
    , $flux.ToArray()
}

$sortie = Join-Path $PSScriptRoot '..\source\icone.ico'
$w = New-Object System.IO.BinaryWriter ([System.IO.File]::Create($sortie))
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$tailles.Count)
$position = 6 + 16 * $tailles.Count
for ($i = 0; $i -lt $tailles.Count; $i++) {
    $t = $tailles[$i]
    $w.Write([byte]($t % 256)); $w.Write([byte]($t % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$position)
    $position += $images[$i].Length
}
foreach ($octets in $images) { $w.Write($octets) }
$w.Close()
"Fichier produit : $sortie"
