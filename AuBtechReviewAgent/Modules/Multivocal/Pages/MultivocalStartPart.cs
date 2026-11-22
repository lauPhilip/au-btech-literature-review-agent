using Microsoft.AspNetCore.Components;

namespace AuBtechReviewAgent.Modules.Multivocal.Pages;

// A part of the grey literature review's page that reads and changes the page's state. Its events are handled by
// the page, so after a click or a keystroke the whole page redraws (the step marks, the settings line, the protocol),
// as when the part's markup was in MultivocalStart.razor.
public abstract class MultivocalStartPart : ComponentBase, IHandleEvent
{
    [Parameter, EditorRequired] public MultivocalStart Page { get; set; } = default!;

    Task IHandleEvent.HandleEventAsync(EventCallbackWorkItem callback, object? arg) =>
        ((IHandleEvent)Page).HandleEventAsync(callback, arg);
}
