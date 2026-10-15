#include "StrideImGuiTextHooks.h"
#include "imgui_internal.h"

static_assert(IMGUI_VERSION_NUM == 19222, "The managed binding pins ImGui 1.92.2b.");
static_assert(sizeof(ImWchar) == 4, "The managed binding requires IMGUI_USE_WCHAR32.");
#if !defined(IMGUI_ENABLE_FREETYPE) || !defined(CIMGUI_VARGS0) || !defined(IMGUI_DISABLE_OBSOLETE_FUNCTIONS)
#error Native text hooks require the pinned Hexa build configuration.
#endif

struct StrideImGuiTabHooks
{
    ImGuiContext* Context;
    void* User;
    StrideImGuiMeasureTab Measure;
    StrideImGuiPaintTab Paint;
};
static ImVector<StrideImGuiTabHooks> Hooks;

static StrideImGuiTabHooks* CurrentHooks()
{
    for (int i = 0; i < Hooks.Size; ++i)
        if (Hooks[i].Context == ImGui::GetCurrentContext()) return &Hooks[i];
    return NULL;
}

unsigned int stride_imgui_text_hook_version() { return 1; }

void stride_imgui_set_tab_text_hooks(ImGuiContext* context, void* user, StrideImGuiMeasureTab measure, StrideImGuiPaintTab paint)
{
    // Owner-thread only, outside tab layout/draw callbacks. Never modifies ImGuiContext storage.
    IM_ASSERT(context != NULL);
    for (int i = 0; i < Hooks.Size; ++i)
        if (Hooks[i].Context == context)
        {
            if (measure == NULL || paint == NULL)
            {
                Hooks.erase(Hooks.Data + i);
                if (Hooks.Size == 0) Hooks.clear();
            }
            else Hooks[i] = { context, user, measure, paint };
            return;
        }
    if (measure != NULL && paint != NULL) Hooks.push_back({ context, user, measure, paint });
}

ImVec2 StrideImGuiTabTextSize(const char* label)
{
    StrideImGuiTabHooks* hooks = CurrentHooks();
    ImVec2 size;
    if (hooks != NULL && hooks->Measure(hooks->User, label, ImGui::FindRenderedTextEnd(label), GImGui->FontSize, &size)
        && size.x >= 0.0f && size.y >= 0.0f && size.x < FLT_MAX && size.y < FLT_MAX)
        return size;
    return ImGui::CalcTextSize(label, NULL, true);
}

bool StrideImGuiPaintTabText(ImDrawList* draw_list, const char* label, ImVec2 minimum, ImVec2 maximum, float ellipsis_max_x)
{
    StrideImGuiTabHooks* hooks = CurrentHooks();
    if (hooks == NULL || !hooks->Paint(hooks->User, draw_list, label, ImGui::FindRenderedTextEnd(label),
        minimum, maximum, ellipsis_max_x, ImGui::GetColorU32(ImGuiCol_Text))) return false;
    // Preserve native logging with original counted UTF8, including label decorations.
    if (GImGui->LogEnabled) ImGui::LogRenderedText(&minimum, label, ImGui::FindRenderedTextEnd(label));
    return true;
}

float StrideImGuiTabLineHeight()
{
    return ImMax(GImGui->FontSize, StrideImGuiTabTextSize("Mg").y);
}

bool StrideImGuiTabOverflowItem(const char* label, bool selected)
{
    if (CurrentHooks() == NULL) return ImGui::Selectable(label, selected);
    ImGuiWindow* window = ImGui::GetCurrentWindow();
    const ImVec2 size = StrideImGuiTabTextSize(label);
    const ImVec2 origin(window->DC.CursorPos.x, window->DC.CursorPos.y + window->DC.CurrLineTextBaseOffset);
    ImGui::PushID(label);
    const bool pressed = ImGui::Selectable("", selected, 0, size);
    ImGui::PopID();
    if (ImGui::IsItemVisible())
    {
        const ImVec2 maximum(GImGui->LastItemData.Rect.Max.x, GImGui->LastItemData.Rect.Max.y);
        if (!StrideImGuiPaintTabText(window->DrawList, label, origin, maximum, -FLT_MAX))
            ImGui::RenderTextClipped(origin, maximum, label, NULL, &size);
    }
    return pressed;
}

bool StrideImGuiPaintWindowTitle(ImDrawList* draw_list, const char* label, ImVec2 minimum, ImVec2 maximum,
    ImVec2 text_size, ImVec2 align, ImVec2 clip_maximum)
{
    const ImVec2 origin(minimum.x + ImMax(0.0f, (maximum.x - minimum.x - text_size.x) * align.x),
        minimum.y + ImMax(0.0f, (maximum.y - minimum.y - text_size.y) * align.y));
    // Negative ellipsis limit means preserve native title clipping rather than synthesize trimming.
    return StrideImGuiPaintTabText(draw_list, label, origin, clip_maximum, -FLT_MAX);
}
