namespace RdpDrop.Client;

internal sealed class MainForm : Form
{
    public MainForm()
    {
        Text = "RdpDrop";
        ClientSize = new Size(1024, 768);
        StartPosition = FormStartPosition.CenterScreen;
        Controls.Add(new Label
        {
            Text = "RdpDrop",
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleCenter,
        });
    }
}
