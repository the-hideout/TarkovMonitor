namespace TarkovMonitor
{
    partial class MainBlazorUI
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Clean up any resources being used.
        /// </summary>
        /// <param name="disposing">true if managed resources should be disposed; otherwise, false.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            components = new System.ComponentModel.Container();
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(MainBlazorUI));
            blazorWebView1 = new Microsoft.AspNetCore.Components.WebView.WindowsForms.BlazorWebView();
            notifyIconTarkovMonitor = new NotifyIcon(components);
            contextMenuStripTarkovMonitor = new ContextMenuStrip(components);
            menuItemQuit = new ToolStripMenuItem();
            contextMenuStripTarkovMonitor.SuspendLayout();
            SuspendLayout();
            // 
            // blazorWebView1
            // 
            blazorWebView1.Dock = DockStyle.Fill;
            blazorWebView1.Location = new Point(0, 0);
            blazorWebView1.Margin = new Padding(3, 4, 3, 4);
            blazorWebView1.Name = "blazorWebView1";
            blazorWebView1.Size = new Size(914, 667);
            blazorWebView1.TabIndex = 0;
            blazorWebView1.Text = "blazorWebView1";
            // 
            // notifyIconTarkovMonitor
            // 
            notifyIconTarkovMonitor.BalloonTipTitle = "Tarkov Monitor";
            notifyIconTarkovMonitor.ContextMenuStrip = contextMenuStripTarkovMonitor;
            notifyIconTarkovMonitor.Icon = (Icon)resources.GetObject("notifyIconTarkovMonitor.Icon");
            notifyIconTarkovMonitor.Text = "Tarkov Monitor";
            notifyIconTarkovMonitor.MouseDoubleClick += notifyIconTarkovMonitor_MouseDoubleClick;
            // 
            // contextMenuStripTarkovMonitor
            // 
            contextMenuStripTarkovMonitor.ImageScalingSize = new Size(20, 20);
            contextMenuStripTarkovMonitor.Items.AddRange(new ToolStripItem[] { menuItemQuit });
            contextMenuStripTarkovMonitor.Name = "contextMenuStripTarkovMonitor";
            contextMenuStripTarkovMonitor.Size = new Size(211, 56);
            // 
            // menuItemQuit
            // 
            menuItemQuit.Name = "menuItemQuit";
            menuItemQuit.Size = new Size(210, 24);
            menuItemQuit.Text = "Quit Tarkov Monitor";
            menuItemQuit.Click += menuItemQuit_Click;
            // 
            // MainBlazorUI
            // 
            AutoScaleDimensions = new SizeF(8F, 20F);
            AutoScaleMode = AutoScaleMode.Font;
            ClientSize = new Size(914, 667);
            Controls.Add(blazorWebView1);
            Icon = (Icon)resources.GetObject("$this.Icon");
            Margin = new Padding(3, 4, 3, 4);
            Name = "MainBlazorUI";
            Text = "Tarkov Monitor";
            Resize += MainBlazorUI_Resize;
            contextMenuStripTarkovMonitor.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private Microsoft.AspNetCore.Components.WebView.WindowsForms.BlazorWebView blazorWebView1;
        private NotifyIcon notifyIconTarkovMonitor;
        private ContextMenuStrip contextMenuStripTarkovMonitor;
        private ToolStripMenuItem menuItemQuit;
    }
}