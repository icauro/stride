param(
    [string]$Source = (Join-Path $PSScriptRoot '../../../Stride.Assets.Presentation.Wpf/View/ImageDictionary.xaml'),
    [string]$Destination = $PSScriptRoot,
    [int]$Pixels = 128,
    [switch]$Components,
    [switch]$Enums,
    [string]$Key,
    [string]$Name
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName WindowsBase, PresentationCore, PresentationFramework
[System.Windows.Media.RenderOptions]::ProcessRenderMode = [System.Windows.Interop.RenderMode]::SoftwareOnly
$culture = [Globalization.CultureInfo]::InvariantCulture
function Number($value) { $value.ToString('R', $culture) }
function MatrixAttribute($transform) {
    if ($null -eq $transform -or $transform.Value.IsIdentity) { return '' }
    $matrix = $transform.Value
    return ' transform="matrix(' + ((@($matrix.M11, $matrix.M12, $matrix.M21, $matrix.M22, $matrix.OffsetX, $matrix.OffsetY) | ForEach-Object { Number $_ }) -join ' ') + ')"'
}
function BrushAttributes($brush, [string]$role) {
    if ($null -eq $brush) { return " $role=`"none`"" }
    if ($brush -is [System.Windows.Media.GradientBrush]) {
        $id = 'brush' + $script:gradientDefinitions.Count
        $units = if ($brush.MappingMode -eq 'Absolute') { 'userSpaceOnUse' } else { 'objectBoundingBox' }
        $spread = switch ($brush.SpreadMethod.ToString()) { 'Pad' { 'pad' }; 'Reflect' { 'reflect' }; 'Repeat' { 'repeat' } }
        $stops = ($brush.GradientStops | ForEach-Object {
            '<stop offset="' + (Number $_.Offset) + '" stop-color="' + ('#{0:X2}{1:X2}{2:X2}' -f $_.Color.R, $_.Color.G, $_.Color.B) + '" stop-opacity="' + (Number ($_.Color.A / 255.0)) + '"/>'
        }) -join ''
        if (-not $brush.Transform.Value.IsIdentity -or -not $brush.RelativeTransform.Value.IsIdentity) { throw 'Transformed gradient brush requires explicit export.' }
        $interpolation = if ($brush.ColorInterpolationMode -eq 'ScRgbLinearInterpolation') { 'linearRGB' } else { 'sRGB' }
        $common = ' id="' + $id + '" gradientUnits="' + $units + '" spreadMethod="' + $spread + '" color-interpolation="' + $interpolation + '"'
        if ($brush -is [System.Windows.Media.RadialGradientBrush]) {
            $transform = 'translate(' + (Number $brush.Center.X) + ' ' + (Number $brush.Center.Y) + ') scale(' + (Number $brush.RadiusX) + ' ' + (Number $brush.RadiusY) + ')'
            $definition = '<radialGradient' + $common + ' cx="0" cy="0" r="1" fx="' + (Number (($brush.GradientOrigin.X - $brush.Center.X) / $brush.RadiusX)) + '" fy="' + (Number (($brush.GradientOrigin.Y - $brush.Center.Y) / $brush.RadiusY)) + '" gradientTransform="' + $transform + '">' + $stops + '</radialGradient>'
        } elseif ($brush -is [System.Windows.Media.LinearGradientBrush]) {
            $definition = '<linearGradient' + $common + ' x1="' + (Number $brush.StartPoint.X) + '" y1="' + (Number $brush.StartPoint.Y) + '" x2="' + (Number $brush.EndPoint.X) + '" y2="' + (Number $brush.EndPoint.Y) + '">' + $stops + '</linearGradient>'
        } else { throw 'Unsupported native gradient brush.' }
        $script:gradientDefinitions.Add($definition)
        return " $role=`"url(#$id)`" $role-opacity=`"$(Number $brush.Opacity)`""
    }
    if ($brush -isnot [System.Windows.Media.SolidColorBrush]) { throw 'Only native solid icon brushes are supported.' }
    $color = $brush.Color
    $rgb = '#{0:X2}{1:X2}{2:X2}' -f $color.R, $color.G, $color.B
    $alpha = ($color.A / 255.0) * $brush.Opacity
    return " $role=`"$rgb`" $role-opacity=`"$(Number $alpha)`""
}
function DrawingSvg($drawing) {
    if ($drawing -is [System.Windows.Media.DrawingGroup]) {
        $children = ($drawing.Children | ForEach-Object { DrawingSvg $_ }) -join "`n"
        return '<g' + (MatrixAttribute $drawing.Transform) + ' opacity="' + (Number $drawing.Opacity) + '">' + "`n" + $children + "`n</g>"
    }
    if ($drawing -isnot [System.Windows.Media.GeometryDrawing]) { throw 'Unsupported native drawing kind.' }
    $geometry = $drawing.Geometry
    $data = $geometry.ToString($culture)
    if ($data -notmatch '^(F[01])?M') { throw 'The native icon geometry is not a serializable path.' }
    $rule = if ($data.StartsWith('F0')) { 'evenodd' } else { 'nonzero' }
    $data = [regex]::Replace($data, '^F[01]', '')
    $attributes = (BrushAttributes $drawing.Brush 'fill') + ' fill-rule="' + $rule + '"' + (MatrixAttribute $geometry.Transform)
    if ($null -ne $drawing.Pen) {
        if ($drawing.Pen.StartLineCap -ne $drawing.Pen.EndLineCap) { throw 'Unequal native stroke caps require explicit export.' }
        $cap = switch ($drawing.Pen.StartLineCap.ToString()) { 'Flat' { 'butt' }; 'Round' { 'round' }; 'Square' { 'square' }; default { throw 'Unsupported native stroke cap.' } }
        $attributes += (BrushAttributes $drawing.Pen.Brush 'stroke') + ' stroke-width="' + (Number $drawing.Pen.Thickness) + '" stroke-linecap="' + $cap + '"'
    }
    return '<path d="' + [Security.SecurityElement]::Escape($data) + '"' + $attributes + '/>'
}
[xml]$dictionary = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $Source))
$namespaces = New-Object Xml.XmlNamespaceManager($dictionary.NameTable)
$namespaces.AddNamespace('p', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
$namespaces.AddNamespace('x', 'http://schemas.microsoft.com/winfx/2006/xaml')
$mapping = [ordered]@{
    ImageLighting = 'scene-toolbar-lighting'
    ImageNavigation = 'scene-toolbar-navigation'
    ImageLightProbesCompute = 'light-probes-compute'
    ImageLightProbesReset = 'light-probes-reset'
    ImageGenerateCubeMap = 'scene-cubemap-capture'
}
if ($Components) {
    $mapping = [ordered]@{}
    foreach ($node in $dictionary.SelectNodes('//p:DrawingImage', $namespaces)) {
        $key = $node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        if ($key -match '^\{x:Type [^:]+:([A-Za-z]+Component)\}$') {
            $mapping[$key] = 'component-' + $Matches[1].ToLowerInvariant()
        }
    }
}
$enumInventory = [Collections.Generic.List[object]]::new()
if ($Enums) {
    if ($Components -or $Key) { throw 'Choose enum inventory, component inventory, or one native key.' }
    $mapping = [ordered]@{}
    foreach ($node in $dictionary.SelectNodes('//p:DrawingImage | //p:BitmapImage', $namespaces)) {
        $enumKey = $node.GetAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
        if ($enumKey -notmatch '^\{x:Static ([^:]+):(.+)\.([^.]+)\}$') { continue }
        $prefix = $Matches[1]; $typeName = $Matches[2]; $valueName = $Matches[3]
        $namespace = $dictionary.DocumentElement.GetAttribute('xmlns:' + $prefix)
        if ($namespace -notmatch '^clr-namespace:([^;]+)(?:;assembly=(.+))?$') { throw "Unresolved native enum namespace: $enumKey" }
        $qualifiedType = $Matches[1] + '.' + $typeName
        $assembly = if ($Matches[2]) { $Matches[2] } else { Split-Path (Split-Path (Split-Path -Path $Source -Parent) -Parent) -Leaf }
        $iconName = 'enum-' + (($assembly + '-' + $qualifiedType + '-' + $valueName).ToLowerInvariant() -replace '[^a-z0-9-]', '-')
        $enumInventory.Add([ordered]@{ assembly = $assembly; type = $qualifiedType; value = $valueName; icon = $iconName })
        if ($node.LocalName -eq 'BitmapImage') {
            $original = Join-Path (Split-Path -Path $Source -Parent) $node.GetAttribute('UriSource')
            Copy-Item -LiteralPath $original -Destination (Join-Path $Destination ($iconName + '.png')) -Force
        } else { $mapping[$enumKey] = $iconName }
    }
}
if ($Key) {
    if (-not $Name -or $Name -notmatch '^[a-z0-9-]+$') { throw 'A native key export requires a simple output Name.' }
    if ($Components) { throw 'Choose component inventory or one native key.' }
    $mapping = [ordered]@{}; $mapping[$Key] = $Name
}
foreach ($key in $mapping.Keys) {
    $node = $dictionary.SelectSingleNode("//p:DrawingImage[@x:Key='$key']", $namespaces)
    if ($null -eq $node) { throw "Missing original icon: $key" }
    $fragment = $node.CloneNode($true)
    $fragment.RemoveAttribute('Key', 'http://schemas.microsoft.com/winfx/2006/xaml')
    $fragment.SetAttribute('xmlns', 'http://schemas.microsoft.com/winfx/2006/xaml/presentation')
    $fragment.SetAttribute('xmlns:x', 'http://schemas.microsoft.com/winfx/2006/xaml')
    $image = [System.Windows.Markup.XamlReader]::Parse($fragment.OuterXml)
    $bounds = $image.Drawing.Bounds
    $viewBox = (@($bounds.X, $bounds.Y, $bounds.Width, $bounds.Height) | ForEach-Object { Number $_ }) -join ' '
    $script:gradientDefinitions = [Collections.Generic.List[string]]::new()
    $drawing = DrawingSvg $image.Drawing
    $definitions = if ($gradientDefinitions.Count) { '<defs>' + ($gradientDefinitions -join '') + '</defs>' } else { '' }
    $svg = '<svg xmlns="http://www.w3.org/2000/svg" viewBox="' + $viewBox + '">' + "`n" + $definitions + $drawing + "`n</svg>"
    [IO.File]::WriteAllText((Join-Path $Destination ($mapping[$key] + '.svg')), $svg)
    $visual = New-Object System.Windows.Media.DrawingVisual
    $context = $visual.RenderOpen()
    try { $context.DrawImage($image, (New-Object System.Windows.Rect(0, 0, $Pixels, $Pixels))) }
    finally { $context.Close() }
    $bitmap = New-Object System.Windows.Media.Imaging.RenderTargetBitmap($Pixels, $Pixels, 96, 96, [System.Windows.Media.PixelFormats]::Pbgra32)
    $bitmap.Render($visual)
    $encoder = New-Object System.Windows.Media.Imaging.PngBitmapEncoder
    $encoder.Frames.Add([System.Windows.Media.Imaging.BitmapFrame]::Create($bitmap))
    $stream = [IO.File]::Create((Join-Path $Destination ($mapping[$key] + '.png')))
    try { $encoder.Save($stream) } finally { $stream.Dispose() }
    Write-Output $mapping[$key]
}
if ($Enums) {
    $manifestPath = Join-Path $Destination 'native-enum-images.json'
    $existing = if (Test-Path -LiteralPath $manifestPath) { @(Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json) } else { @() }
    $identities = @{}; foreach ($entry in $enumInventory) { $identities[$entry.assembly + '/' + $entry.type + '/' + $entry.value] = $true }
    $retained = @($existing | Where-Object { -not $identities.ContainsKey($_.assembly + '/' + $_.type + '/' + $_.value) })
    ConvertTo-Json -InputObject @($retained + $enumInventory.ToArray()) -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
}
