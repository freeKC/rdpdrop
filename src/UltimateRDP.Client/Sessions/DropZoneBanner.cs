using UltimateRDP.Client.Resources;

namespace UltimateRDP.Client.Sessions;

/// <summary>
/// Translucent banner shown at the top of the session while files are dragged over it.
/// </summary>
/// <remarks>
/// A child control cannot be translucent over the ActiveX control, so the banner is a
/// small layered window. It lets the mouse through (WS_EX_TRANSPARENT), so OLE never
/// takes it for the drop target and the drag keeps reaching the session below it, and it
/// never takes the focus.
/// </remarks>
internal sealed class DropZoneBanner : Form
{
    /// <summary>WS_EX_TRANSPARENT: hit testing passes through the window.</summary>
    private const int WsExTransparent = 0x00000020;

    /// <summary>WS_EX_TOOLWINDOW: no taskbar button and no Alt+Tab entry.</summary>
    private const int WsExToolWindow = 0x00000080;

    /// <summary>WS_EX_LAYERED: the window has an opacity.</summary>
    private const int WsExLayered = 0x00080000;

    /// <summary>WS_EX_NOACTIVATE: the window never comes to the foreground.</summary>
    private const int WsExNoActivate = 0x08000000;

    /// <summary>Height of the banner at 96 DPI.</summary>
    private const int BannerHeight = 36;

    private readonly Label _label;
    private readonly Font _font;

    public DropZoneBanner()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(0, 90, 158);
        Opacity = 0.85;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;

        // The font of the form, which the label inherits: Windows Forms scales it to the
        // DPI of the monitor the banner goes to.
        using (var messageFont = SystemFonts.MessageBoxFont)
        {
            _font = new Font(messageFont ?? Control.DefaultFont, FontStyle.Bold);
        }

        Font = _font;
        _label = new Label
        {
            Dock = DockStyle.Fill,
            ForeColor = Color.White,
            TextAlign = ContentAlignment.MiddleCenter,
        };
        Controls.Add(_label);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExLayered | WsExTransparent | WsExToolWindow | WsExNoActivate;
            return parameters;
        }
    }

    /// <summary>
    /// Shows the banner over the top of <paramref name="screenArea"/>, the screen area of
    /// the session. <paramref name="topMost"/> is needed over a full screen session.
    /// </summary>
    public void ShowOver(Rectangle screenArea, Form? owner, bool topMost)
    {
        // Read at each show, so that the banner follows a change of language.
        var text = Strings.DropZoneBanner;
        if (_label.Text != text)
        {
            _label.Text = text;
        }

        Place(screenArea);
        if (!Visible)
        {
            // Changing TopMost on a visible window could activate it.
            TopMost = topMost;
            if (owner is not null && Owner != owner)
            {
                Owner = owner;
            }

            Show();

            // The window now exists on the monitor of the session, with its DPI.
            Place(screenArea);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Covers the top of <paramref name="screenArea"/>. A move to a monitor with another
    /// DPI makes Windows Forms scale the banner and propose other bounds, so the bounds
    /// are computed again, once, with the height for the new DPI.
    /// </summary>
    private void Place(Rectangle screenArea)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            var height = Math.Min(BannerHeight * DeviceDpi / 96, Math.Max(1, screenArea.Height));
            var bounds = new Rectangle(screenArea.X, screenArea.Y, screenArea.Width, height);
            if (Bounds == bounds)
            {
                return;
            }

            Bounds = bounds;
        }
    }
}
