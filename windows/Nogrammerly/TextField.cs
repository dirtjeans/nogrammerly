using System.Drawing;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Patterns;
using Nogrammerly.Core;

namespace Nogrammerly;

/// <summary>
/// An editable text control in another app, read and edited through Windows UI Automation
/// (what screen readers use). Offsets are UTF-16 code units, which is how UI Automation counts characters.
/// All calls are cross-process, so use it only from <see cref="SystemWritingAssistant"/>'s worker thread.
///
/// There are two kinds. A text box holds a little text and is read whole. A document (a word processor's
/// page, such as Word's) can hold a book, so it is handled through the part on screen: positions are
/// measured from the first visible line, and a document too long to check whole is checked a screenful at a time.
/// </summary>
internal sealed class TextField
{
    public AutomationElement Element { get; }
    public int ProcessId { get; }
    private readonly ITextPattern? textPattern;
    private readonly IValuePattern? valuePattern;
    private readonly bool isDocument;

    // For documents, set by ReadText: a range that starts at a known place on screen, where that place is in
    // the text ReadText returned, and where the visible part ends. Stepping a range character by character
    // from the start of a long document takes Word about half a second, from here almost nothing.
    private ITextRange? anchor;
    private int anchorOffset;
    private int visibleEnd;

    private TextField(AutomationElement element, ITextPattern? text, IValuePattern? value, int processId, bool isDocument)
    {
        Element = element; textPattern = text; valuePattern = value; ProcessId = processId; this.isDocument = isDocument;
    }

    /// <summary>True when underlines can be positioned; fields without a text pattern only get the menu.</summary>
    public bool HasLayout => textPattern is not null;

    /// <summary>True when the text read is only the part of a long document that is on screen.</summary>
    public bool IsPartial { get; private set; }

    /// <summary>
    /// Wraps <paramref name="element"/> if it is editable text. Password fields are never read, and read-only
    /// text that still exposes a text pattern (terminal scrollback, web pages, documents in viewers) is skipped.
    /// </summary>
    public static TextField? From(AutomationElement? element)
    {
        if (element is null) return null;
        try
        {
            if (element.Properties.IsPassword.ValueOrDefault) return null;
            var text = element.Patterns.Text.PatternOrDefault;
            var value = element.Patterns.Value.PatternOrDefault;
            var type = element.Properties.ControlType.ValueOrDefault;
            // A document with no value to ask "are you read-only?" says so through its text instead.
            var isDocument = value is null && text is not null && type == ControlType.Document;
            var editable = value is not null
                ? !value.IsReadOnly.ValueOrDefault
                : text is not null && (type == ControlType.Edit || (isDocument && CanTypeAtCursor(element, text)));
            if (!editable || (text is null && value is null)) return null;
            return new TextField(element, text, value, element.Properties.ProcessId.ValueOrDefault, isDocument);
        }
        catch (Exception)
        {
            return null; // The element vanished or its app stopped responding.
        }
    }

    /// <summary>True only when the app says outright that the text at the cursor can be edited.</summary>
    private static bool CanTypeAtCursor(AutomationElement element, ITextPattern text)
    {
        try
        {
            var cursor = text.GetSelection().FirstOrDefault();
            return cursor?.GetAttributeValue(element.Automation.TextAttributeLibrary.IsReadOnly) is false;
        }
        catch (Exception) { return false; }
    }

    public bool IsSameElement(TextField other)
    {
        try { return Element.Equals(other.Element); }
        catch (Exception) { return false; }
    }

    /// <summary>The text to check. Call it before the methods that take positions: for a document it also notes what is on screen.</summary>
    public string? ReadText()
    {
        try
        {
            if (textPattern is null) return valuePattern?.Value.ValueOrDefault;
            return isDocument ? ReadDocument(textPattern) : textPattern.DocumentRange.GetText(-1);
        }
        catch (Exception) { return null; }
    }

    private string ReadDocument(ITextPattern pattern)
    {
        anchor = null;
        var whole = pattern.DocumentRange.GetText(WritingEngine.MaxLength + 1);
        IsPartial = whole.Length > WritingEngine.MaxLength;
        var visible = pattern.GetVisibleRanges();
        if (visible.Length == 0) return IsPartial ? "" : whole;

        if (!IsPartial)
        {
            var before = pattern.DocumentRange.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, visible[0], TextPatternRangeEndpoint.Start);
            var shown = visible[0].Clone();
            shown.MoveEndpointByRange(TextPatternRangeEndpoint.End, visible[^1], TextPatternRangeEndpoint.End);
            anchor = visible[0];
            anchorOffset = before.GetText(-1).Length;
            visibleEnd = anchorOffset + shown.GetText(-1).Length;
            return whole;
        }

        // Too long to check whole: take the paragraphs on screen, complete, so no sentence is cut in half.
        var scope = visible[0].Clone();
        scope.ExpandToEnclosingUnit(TextUnit.Paragraph);
        var last = visible[^1].Clone();
        last.ExpandToEnclosingUnit(TextUnit.Paragraph);
        scope.MoveEndpointByRange(TextPatternRangeEndpoint.End, last, TextPatternRangeEndpoint.End);
        var text = scope.GetText(WritingEngine.MaxLength);
        anchor = scope;
        anchorOffset = 0;
        visibleEnd = text.Length;
        return text;
    }

    /// <summary>The control's on-screen rectangle, in physical pixels.</summary>
    public Rectangle? Frame()
    {
        try
        {
            var frame = Element.Properties.BoundingRectangle.ValueOrDefault;
            return frame.Width > 0 && frame.Height > 0 ? frame : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>False when <paramref name="span"/> is known to be off screen, so there is no point asking where it is.</summary>
    public bool MayBeVisible(TextSpan span) =>
        !isDocument || (anchor is not null && span.End >= anchorOffset && span.Start <= visibleEnd);

    /// <summary>One rectangle per visible line of <paramref name="span"/>; text scrolled out of view has none.</summary>
    public Rectangle[] Bounds(TextSpan span)
    {
        try { return MayBeVisible(span) ? RangeAt(span)?.GetBoundingRectangles() ?? [] : []; }
        catch (Exception) { return []; }
    }

    /// <summary>The text cursor's rectangle on screen, or null if there is a selection or the app doesn't say.</summary>
    public Rectangle? Caret()
    {
        try
        {
            var selection = textPattern?.GetSelection().FirstOrDefault();
            if (selection is null || selection.GetText(1).Length > 0) return null;
            // An empty range has no rectangle in most apps, so measure the character after the cursor,
            // or the one before it at the end of the text.
            var probe = selection.Clone();
            if (probe.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, 1) == 0)
                probe.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, -1);
            var rect = probe.GetBoundingRectangles().FirstOrDefault();
            return rect is { Width: > 0, Height: > 0 and < 200 } ? rect : null;
        }
        catch (Exception) { return null; }
    }

    public bool HasSelectedText()
    {
        try { return textPattern?.GetSelection().FirstOrDefault()?.GetText(1).Length > 0; }
        catch (Exception) { return false; }
    }

    /// <summary>The selection as a position in the text ReadText returned; null if there is none or it lies outside that text.</summary>
    public TextSpan? Selection()
    {
        try
        {
            if (textPattern is null) return null;
            var selection = textPattern.GetSelection().FirstOrDefault();
            if (selection is null) return null;
            var start = textPattern.DocumentRange;
            if (IsPartial)
            {
                // Only a selection wholly inside the part being checked can be placed.
                if (anchor is null
                    || selection.CompareEndpoints(TextPatternRangeEndpoint.Start, anchor, TextPatternRangeEndpoint.Start) < 0
                    || selection.CompareEndpoints(TextPatternRangeEndpoint.End, anchor, TextPatternRangeEndpoint.End) > 0) return null;
                start = anchor;
            }
            var before = start.Clone();
            before.MoveEndpointByRange(TextPatternRangeEndpoint.End, selection, TextPatternRangeEndpoint.Start);
            return new TextSpan(before.GetText(-1).Length, selection.GetText(-1).Length);
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// Replaces <paramref name="span"/> with <paramref name="replacement"/>, keeping the app's undo history:
    /// it selects the span and types over it. Fields that can't select text fall back to setting the whole value.
    /// </summary>
    public bool Replace(TextSpan span, string replacement, string currentText)
    {
        try
        {
            if (RangeAt(span) is { } range)
            {
                // Typing replaces whatever is selected, so be sure the selection is the text that was checked.
                // Apps don't all count characters alike (pictures, tables, fields), and a wrong count must not edit.
                if (isDocument && TextAt(span) != currentText.Substring(span.Start, span.Length)) return false;
                range.Select();
                Thread.Sleep(30);
                // Typing goes to whichever window is in front; never type into anything but this field's app.
                return NativeMethods.ForegroundProcessId() == ProcessId && NativeMethods.TypeText(replacement);
            }
            if (valuePattern is not null && textPattern is null)
            {
                valuePattern.SetValue(currentText.Remove(span.Start, span.Length).Insert(span.Start, replacement));
                return true;
            }
        }
        catch (Exception) { }
        return false;
    }

    /// <summary>What the app itself holds at <paramref name="span"/>, to check that positions line up; null if it can't say.</summary>
    internal string? TextAt(TextSpan span)
    {
        try { return RangeAt(span)?.GetText(-1); }
        catch (Exception) { return null; }
    }

    private ITextRange? RangeAt(TextSpan span)
    {
        if (textPattern is null) return null;
        if (!isDocument)
        {
            var whole = textPattern.DocumentRange.Clone();
            whole.MoveEndpointByRange(TextPatternRangeEndpoint.End, whole, TextPatternRangeEndpoint.Start);
            whole.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, span.End);
            whole.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, span.Start);
            return whole;
        }
        if (anchor is null) return null;
        // Start as an empty range at the anchor, then step each end to its place. The end that leads goes first,
        // because an end pushed past the other drags it along.
        var range = anchor.Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        int toStart = span.Start - anchorOffset, toEnd = span.End - anchorOffset;
        var first = toStart < 0 ? TextPatternRangeEndpoint.Start : TextPatternRangeEndpoint.End;
        foreach (var end in new[] { first, first == TextPatternRangeEndpoint.Start ? TextPatternRangeEndpoint.End : TextPatternRangeEndpoint.Start })
        {
            var steps = end == TextPatternRangeEndpoint.Start ? toStart : toEnd;
            if (steps != 0 && range.MoveEndpointByUnit(end, TextUnit.Character, steps) != steps) return null;
        }
        return range;
    }
}
