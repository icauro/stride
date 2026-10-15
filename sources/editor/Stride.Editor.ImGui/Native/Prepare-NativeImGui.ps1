param(
    [Parameter(Mandatory=$true)][string] $SourceRoot,
    [string] $LuaJit = 'luajit',
    [ValidateSet('gcc','clang','cl')][string] $Preprocessor = 'gcc'
)
$ErrorActionPreference = 'Stop'
$imguiCommit = '1f7f1f54af38b0350d8c0008b096a9af6de299c7'
$generatorCommit = '207fca2d361179c349f3c9d1893b8274f4bbfebf'
$bindingCommit = 'ca764cc25238aa2e68f44ee189c5cc80453ea178'
$expectedHeaderHash = '29D144F7F40EAFFC50936170DA9D85FF5F5BAD3B184CEE977A334942DC65E91B'
$sourcePath = [IO.Path]::GetFullPath($SourceRoot)
if (Test-Path -LiteralPath $sourcePath) { throw 'Use a new private source directory; preparation never overwrites an existing tree.' }
Get-Command $LuaJit -ErrorAction Stop | Out-Null
Get-Command $Preprocessor -ErrorAction Stop | Out-Null
New-Item -ItemType Directory -Path $sourcePath | Out-Null

function Get-PinnedArchive([string] $repository, [string] $revision, [string] $destination) {
    $archivePath = Join-Path $sourcePath ($revision + '.zip')
    Invoke-WebRequest "https://codeload.github.com/$repository/zip/$revision" -OutFile $archivePath
    $expandedPath = Join-Path $sourcePath ($revision + '-archive')
    [IO.Compression.ZipFile]::ExtractToDirectory($archivePath, $expandedPath)
    $directories = @(Get-ChildItem -LiteralPath $expandedPath -Directory)
    if ($directories.Count -ne 1) { throw 'Unexpected pinned archive layout.' }
    Move-Item -LiteralPath $directories[0].FullName -Destination $destination
}

$cimguiPath = Join-Path $sourcePath 'cimgui'
Get-PinnedArchive 'JunaMeinhold/cimgui' $generatorCommit $cimguiPath
# Git archives do not contain the imgui submodule. Never use its moving branch.
$imguiPath = Join-Path $cimguiPath 'imgui'
if (Test-Path -LiteralPath $imguiPath) {
    if (@(Get-ChildItem -LiteralPath $imguiPath -Force).Count -ne 0) { throw 'Unexpected populated imgui submodule.' }
    Remove-Item -LiteralPath $imguiPath # Empty directory only.
}
Get-PinnedArchive 'ocornut/imgui' $imguiCommit $imguiPath
$baselinePath = Join-Path $sourcePath 'Hexa-2.2.9-cimgui.h'
Invoke-WebRequest "https://raw.githubusercontent.com/HexaEngine/Hexa.NET.ImGui/$bindingCommit/Generator/cimgui/cimgui.h" -OutFile $baselinePath
$baselineBytes = [IO.File]::ReadAllBytes($baselinePath)
if ([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($baselineBytes)) -ne $expectedHeaderHash) {
    throw 'Pinned Hexa header bytes differ; no ABI approval or native artifact will be produced.'
}

Push-Location (Join-Path $cimguiPath 'generator')
try {
    & $LuaJit './generator.lua' $Preprocessor 'internal noimstrv'
    if ($LASTEXITCODE -ne 0) { throw "Native wrapper generation failed ($LASTEXITCODE)." }
} finally { Pop-Location }

# Compare every significant C header token, including fields, enums, calling conventions,
# wchar branches and all public/private exports. Ignore only comments and whitespace.
# A generator difference is a hard failure, not permission to replace the binding ABI.
function Get-HeaderTokens([string] $path) {
    $text = [IO.File]::ReadAllText($path)
    $pattern = '"(?:\\.|[^"\\])*"|''(?:\\.|[^''\\])*''|/\*[\s\S]*?\*/|//[^\r\n]*|[A-Za-z_][A-Za-z_0-9]*|[0-9]+|[^\s]'
    $tokens = [Collections.Generic.List[string]]::new()
    foreach ($match in [regex]::Matches($text, $pattern)) {
        if ($match.Value.StartsWith('/*') -or $match.Value.StartsWith('//')) { continue }
        $tokens.Add($match.Value)
    }
    return ,$tokens.ToArray()
}
$generatedHeader = Join-Path $cimguiPath 'generator/output/cimgui.h'
$expectedTokens = Get-HeaderTokens $baselinePath
$actualTokens = Get-HeaderTokens $generatedHeader
if ($expectedTokens.Count -ne $actualTokens.Count) { throw 'Generated header token count differs from pinned Hexa ABI.' }
for ($index = 0; $index -lt $expectedTokens.Count; $index++) {
    if ($expectedTokens[$index] -cne $actualTokens[$index]) {
        throw "Generated header differs from pinned Hexa ABI at token $index. No DLL may be staged."
    }
}
Copy-Item -LiteralPath $generatedHeader -Destination (Join-Path $cimguiPath 'cimgui.h')
Copy-Item -LiteralPath (Join-Path $cimguiPath 'generator/output/cimgui.cpp') -Destination (Join-Path $cimguiPath 'cimgui.cpp')

function Replace-Exactly([string] $text, [string] $old, [string] $replacement, [int] $count) {
    if ([regex]::Matches($text, [regex]::Escape($old)).Count -ne $count) { throw 'Pinned native patch context differs.' }
    return $text.Replace($old, $replacement)
}
$widgetsPath = Join-Path $imguiPath 'imgui_widgets.cpp'
$widgets = [IO.File]::ReadAllText($widgetsPath).Replace("`r`n", "`n")
$widgets = Replace-Exactly $widgets '#include "imgui_internal.h"' "#include `"imgui_internal.h`"`n#include `"StrideImGuiTextHooks.h`"" 1
foreach ($signature in @(
    'ImVec2 ImGui::TabItemCalcSize(const char* label, bool has_close_button_or_unsaved_marker)',
    'void ImGui::TabItemLabelAndCloseButton(ImDrawList* draw_list, const ImRect& bb, ImGuiTabItemFlags flags, ImVec2 frame_padding, const char* label, ImGuiID tab_id, ImGuiID close_button_id, bool is_contents_visible, bool* out_just_closed, bool* out_text_clipped)')) {
    $old = $signature + "`n{`n    ImGuiContext& g = *GImGui;`n    ImVec2 label_size = CalcTextSize(label, NULL, true);"
    $widgets = Replace-Exactly $widgets $old ($old.Replace('CalcTextSize(label, NULL, true)', 'StrideImGuiTabTextSize(label)')) 1
}
$oldPaint = '    RenderTextEllipsis(draw_list, text_ellipsis_clip_bb.Min, text_ellipsis_clip_bb.Max, ellipsis_max_x, label, NULL, &label_size);'
$widgets = Replace-Exactly $widgets $oldPaint ("    if (!StrideImGuiPaintTabText(draw_list, label, text_ellipsis_clip_bb.Min, text_ellipsis_clip_bb.Max, ellipsis_max_x))`n    " + $oldPaint) 1
$widgets = Replace-Exactly $widgets 'window->DC.CursorPos.y + g.FontSize + g.Style.FramePadding.y * 2);' 'window->DC.CursorPos.y + StrideImGuiTabLineHeight() + g.Style.FramePadding.y * 2);' 1
$widgets = Replace-Exactly $widgets 'if (Selectable(tab_name, tab_bar->SelectedTabId == tab->ID))' 'if (StrideImGuiTabOverflowItem(tab_name, tab_bar->SelectedTabId == tab->ID))' 1
[IO.File]::WriteAllText($widgetsPath, $widgets, [Text.UTF8Encoding]::new($false))

# Registration lifetime ends at actual native context destruction, including failed startup.
$contextPath = Join-Path $imguiPath 'imgui.cpp'
$contextSource = [IO.File]::ReadAllText($contextPath).Replace("`r`n", "`n")
$contextSource = Replace-Exactly $contextSource '#include "imgui_internal.h"' "#include `"imgui_internal.h`"`n#include `"StrideImGuiTextHooks.h`"" 1
$destroy = "void ImGui::DestroyContext(ImGuiContext* ctx)`n{"
$contextSource = Replace-Exactly $contextSource $destroy ($destroy + "`n    if (ctx == NULL) ctx = GImGui;`n    if (ctx != NULL) stride_imgui_set_tab_text_hooks(ctx, NULL, NULL, NULL);") 1
$contextSource = Replace-Exactly $contextSource 'window->TitleBarHeight = (flags & ImGuiWindowFlags_NoTitleBar) ? 0.0f : g.FontSize + g.Style.FramePadding.y * 2.0f;' 'window->TitleBarHeight = (flags & ImGuiWindowFlags_NoTitleBar) ? 0.0f : StrideImGuiTabLineHeight() + g.Style.FramePadding.y * 2.0f;' 1
$contextSource = Replace-Exactly $contextSource 'ImRect r = ImRect(node->Pos.x, node->Pos.y, node->Pos.x + node->Size.x, node->Pos.y + g.FontSize + g.Style.FramePadding.y * 2.0f);' 'ImRect r = ImRect(node->Pos.x, node->Pos.y, node->Pos.x + node->Size.x, node->Pos.y + StrideImGuiTabLineHeight() + g.Style.FramePadding.y * 2.0f);' 1
$contextSource = Replace-Exactly $contextSource 'const ImVec2 text_size = CalcTextSize(name, NULL, true) + ImVec2(marker_size_x, 0.0f);' 'const ImVec2 text_size = StrideImGuiTabTextSize(name) + ImVec2(marker_size_x, 0.0f);' 1
$oldTitlePaint = '    RenderTextClipped(layout_r.Min, layout_r.Max, name, NULL, &text_size, style.WindowTitleAlign, &clip_r);'
$contextSource = Replace-Exactly $contextSource $oldTitlePaint ("    if (!StrideImGuiPaintWindowTitle(window->DrawList, name, layout_r.Min, layout_r.Max, text_size, style.WindowTitleAlign, clip_r.Max))`n    " + $oldTitlePaint) 1
$contextSource = Replace-Exactly $contextSource 'if (Selectable(TabBarGetTabName(tab_bar, tab), tab->ID == tab_bar->SelectedTabId))' 'if (StrideImGuiTabOverflowItem(TabBarGetTabName(tab_bar, tab), tab->ID == tab_bar->SelectedTabId))' 1
[IO.File]::WriteAllText($contextPath, $contextSource, [Text.UTF8Encoding]::new($false))
$approvedInputs = @{}
foreach ($relativePath in @('cimgui.h','cimgui.cpp','cimconfig.h','CMakeLists.txt','imgui/imgui.h','imgui/imgui_internal.h',
    'imgui/imgui.cpp','imgui/imgui_widgets.cpp','imgui/imgui_draw.cpp','imgui/imgui_tables.cpp',
    'imgui/misc/freetype/imgui_freetype.h','imgui/misc/freetype/imgui_freetype.cpp')) {
    $approvedInputs[$relativePath] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [IO.File]::ReadAllBytes((Join-Path $cimguiPath $relativePath))))
}

@{
    ProtocolVersion=1; ImGuiCommit=$imguiCommit; GeneratorCommit=$generatorCommit; BindingCommit=$bindingCommit
    PinnedHeaderSha256=$expectedHeaderHash
    GeneratedHeaderSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($generatedHeader)))
    Configuration=@('IMGUI_USE_WCHAR32','IMGUI_ENABLE_FREETYPE','IMGUI_ENABLE_STB_TRUETYPE','CIMGUI_VARGS0','IMGUI_DISABLE_OBSOLETE_FUNCTIONS')
    Inputs=$approvedInputs
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $cimguiPath 'stride-abi-approved.json') -Encoding utf8
Write-Output $cimguiPath
