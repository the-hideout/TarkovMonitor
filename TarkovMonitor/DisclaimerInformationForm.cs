using System.Drawing;
using System.Runtime.InteropServices;

namespace TarkovMonitor;

internal sealed class DisclaimerInformationForm : Form
{
    private const int DwmWindowCornerPreference = 33;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmRound = 2;
    private const int TarkovBorderColor = 0x003B555F;
    private const int TarkovHeaderColor = 0x002D2F2F;
    private const int WmNcLButtonDown = 0x00A1;
    private const int HtCaption = 0x0002;

    private static readonly Color Surface = Color.FromArgb(30, 30, 30);
    private static readonly Color Header = Color.FromArgb(45, 47, 47);
    private static readonly Color Card = Color.FromArgb(47, 47, 45);
    private static readonly Color PrimaryText = Color.FromArgb(235, 235, 235);
    private static readonly Color SecondaryText = Color.FromArgb(190, 190, 190);
    private static readonly Color Accent = Color.FromArgb(116, 188, 214);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);

    public DisclaimerInformationForm(Form owner)
    {
        Text = "Disclaimer Information";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(720, 520);
        ClientSize = new Size(760, 600);
        BackColor = Surface;
        ForeColor = PrimaryText;

        var header = BuildHeader();
        var body = BuildBody();
        Controls.Add(body);
        Controls.Add(header);

        Owner = owner;
    }

    private Control BuildHeader()
    {
        var header = new Panel
        {
            Dock = DockStyle.Top,
            Height = 46,
            BackColor = Header,
            Padding = new Padding(16, 0, 8, 0),
        };
        header.MouseDown += DragHeader;

        var close = new Button
        {
            Dock = DockStyle.Right,
            Width = 40,
            Text = "X",
            FlatStyle = FlatStyle.Flat,
            Font = new Font("Segoe UI", 14F, FontStyle.Regular),
            ForeColor = PrimaryText,
            BackColor = Header,
            TabStop = false,
        };
        close.FlatAppearance.BorderSize = 0;
        close.Click += (_, _) => Close();

        var title = new Label
        {
            Dock = DockStyle.Fill,
            Text = "Disclaimer Information",
            TextAlign = ContentAlignment.MiddleLeft,
            Font = new Font("Segoe UI Semibold", 10F, FontStyle.Regular),
            ForeColor = PrimaryText,
            BackColor = Header,
        };
        title.MouseDown += DragHeader;

        header.Controls.Add(close);
        header.Controls.Add(title);
        return header;
    }

    private Control BuildBody()
    {
        var scroll = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Surface,
            Padding = new Padding(20),
        };

        var content = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            BackColor = Surface,
            Width = 680,
        };

        content.Controls.Add(CreateCard(
            "Before you share diagnostics",
            "TarkovMonitor creates a short on-screen failure message and a more detailed diagnostic record for troubleshooting. The Copy diagnostics button copies the detailed, sanitized record; it does not copy the raw exception object or raw EFT log."));
        content.Controls.Add(CreateCard(
            "Included",
            "Diagnostic code and ID, operation and service boundary, failure stage, UTC time, duration, application/runtime/Windows information, HTTP status or socket error metadata when available, and the preserved inner-exception chain."));
        content.Controls.Add(CreateCard(
            "Excluded or redacted",
            "API keys, bearer tokens, authorization headers, cookies, request bodies, query strings, local/public IP addresses, EFT account/profile identifiers, remote IDs, nicknames, chat, raw EFT log lines, full local paths, and full URLs with query data are excluded or redacted."));
        content.Controls.Add(CreateCard(
            "Local storage and sharing",
            "Sanitized diagnostic records and aggregate failure analytics are kept locally under %LOCALAPPDATA%\\TarkovMonitor\\Diagnostics in rotating files. TarkovMonitor does not automatically upload these diagnostics. Review the copied text before posting it in Discord, an issue, or another public channel."));
        content.Controls.Add(CreateCard(
            "Network privacy",
            "The normal network request that failed may still be visible to your network, VPN, hotspot provider, proxy, security software, or the third-party service. That is separate from the diagnostic record and is not copied into it."));
        content.Controls.Add(CreateCard(
            "What to send support",
            "Send the complete copied diagnostic text, the short message shown in TarkovMonitor, whether a browser page also failed, and whether the result changed on a hotspot or with a VPN disabled. Do not send API keys, screenshots containing tokens, or raw EFT logs unless support specifically requests a reviewed excerpt."));

        scroll.Controls.Add(content);
        return scroll;
    }

    private static Panel CreateCard(string heading, string text)
    {
        var card = new Panel
        {
            Width = 680,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            BackColor = Card,
            Padding = new Padding(14),
            Margin = new Padding(0, 0, 0, 12),
        };

        var body = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            Text = text,
            ForeColor = SecondaryText,
            BackColor = Card,
            Font = new Font("Segoe UI", 9F, FontStyle.Regular),
            Padding = new Padding(0, 7, 0, 0),
        };
        var title = new Label
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            MaximumSize = new Size(650, 0),
            Text = heading,
            ForeColor = Accent,
            BackColor = Card,
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Regular),
        };

        card.Controls.Add(body);
        card.Controls.Add(title);
        return card;
    }

    private void DragHeader(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        ReleaseCapture();
        SendMessage(Handle, WmNcLButtonDown, (IntPtr)HtCaption, IntPtr.Zero);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        var cornerPreference = DwmRound;
        DwmSetWindowAttribute(Handle, DwmWindowCornerPreference, ref cornerPreference, sizeof(int));
        var borderColor = TarkovBorderColor;
        DwmSetWindowAttribute(Handle, DwmBorderColor, ref borderColor, sizeof(int));
        var captionColor = TarkovHeaderColor;
        DwmSetWindowAttribute(Handle, DwmCaptionColor, ref captionColor, sizeof(int));
    }
}
