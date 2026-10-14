namespace Compositor.Desktop;

public sealed partial class MainWindow
{
    private bool _finishingEdits;

    /// <summary>Finish work on its owning document before saving, switching tabs or closing it.</summary>
    private void FinishActiveEdits()
    {
        if (_finishingEdits) return;
        _finishingEdits = true;
        try
        {
            InvalidatePendingClipboardPaste();
            // Restore temporary previews before a captured gesture writes into the document.
            CloseColorRange();
            CloseCameraRaw();
            StopPreview();
            _canvas.FinishInteraction();
            GuidePulledOff();
            FinishOpacityEdit();
            CommitText();
        }
        finally { _finishingEdits = false; }
    }

    private void FinishOpacityEdit()
    {
        if (!_opacityDragging) return;
        _opacityDragging = false;
        if (_document is not { } document) return;
        try { ApplyOpacity(); }
        finally
        {
            _history.End(document, Selected);
            Refresh();
        }
    }

    /// <summary>Keep commands outside live text and finish work before its editing target can change.</summary>
    private bool MayEditImage()
    {
        if (_text is not null)
        {
            Say("Finish text editing before changing the image.");
            return false;
        }
        FinishActiveEdits();
        return true;
    }
}
