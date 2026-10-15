#pragma once
#include "imgui.h"

// Separate callback state; the pinned ImGuiContext and all generated ABI types stay unchanged.
typedef bool (*StrideImGuiMeasureTab)(void* user, const char* begin, const char* end, float font_size, ImVec2* result);
typedef bool (*StrideImGuiPaintTab)(void* user, ImDrawList* draw_list, const char* begin, const char* end,
    ImVec2 minimum, ImVec2 maximum, float ellipsis_max_x, ImU32 color);

extern "C" __declspec(dllexport) unsigned int stride_imgui_text_hook_version();
extern "C" __declspec(dllexport) void stride_imgui_set_tab_text_hooks(ImGuiContext* context, void* user,
    StrideImGuiMeasureTab measure, StrideImGuiPaintTab paint);
ImVec2 StrideImGuiTabTextSize(const char* label);
bool StrideImGuiPaintTabText(ImDrawList* draw_list, const char* label, ImVec2 minimum, ImVec2 maximum, float ellipsis_max_x);
float StrideImGuiTabLineHeight();
bool StrideImGuiTabOverflowItem(const char* label, bool selected);
bool StrideImGuiPaintWindowTitle(ImDrawList* draw_list, const char* label, ImVec2 minimum, ImVec2 maximum,
    ImVec2 text_size, ImVec2 align, ImVec2 clip_maximum);
