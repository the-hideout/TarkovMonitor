namespace TarkovMonitor
{
    partial class MainWindow
    {
        /// <summary>
        ///  Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        ///  Clean up any resources being used.
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
        ///  Required method for Designer support - do not modify
        ///  the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.txtMessages = new MaterialSkin.Controls.MaterialMultiLineTextBox();
            this.chkQueue = new MaterialSkin.Controls.MaterialCheckbox();
            this.btnTestToken = new MaterialSkin.Controls.MaterialButton();
            this.txtToken = new MaterialSkin.Controls.MaterialTextBox();
            this.materialTabSelector1 = new MaterialSkin.Controls.MaterialTabSelector();
            this.tabsMain = new MaterialSkin.Controls.MaterialTabControl();
            this.tabMessages = new System.Windows.Forms.TabPage();
            this.tabLoadouts = new System.Windows.Forms.TabPage();
            this.listBoxLoadout = new MaterialSkin.Controls.MaterialListBox();
            this.comboGroupMembers = new MaterialSkin.Controls.MaterialComboBox();
            this.tabSettings = new System.Windows.Forms.TabPage();
            this.btnPlayRaidSound = new MaterialSkin.Controls.MaterialButton();
            this.chkRaidStartAlert = new MaterialSkin.Controls.MaterialCheckbox();
            this.btnTarkovTrackerLink = new MaterialSkin.Controls.MaterialButton();
            this.btnCancelSettings = new MaterialSkin.Controls.MaterialButton();
            this.btnSaveSettings = new MaterialSkin.Controls.MaterialButton();
            this.tabLogs = new System.Windows.Forms.TabPage();
            this.txtLogs = new MaterialSkin.Controls.MaterialMultiLineTextBox();
            this.tabsMain.SuspendLayout();
            this.tabMessages.SuspendLayout();
            this.tabLoadouts.SuspendLayout();
            this.tabSettings.SuspendLayout();
            this.tabLogs.SuspendLayout();
            this.SuspendLayout();
            // 
            // txtMessages
            // 
            this.txtMessages.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(255)))), ((int)(((byte)(255)))), ((int)(((byte)(255)))));
            this.txtMessages.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.txtMessages.Depth = 0;
            this.txtMessages.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtMessages.Font = new System.Drawing.Font("Microsoft Sans Serif", 14F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.txtMessages.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(222)))), ((int)(((byte)(0)))), ((int)(((byte)(0)))), ((int)(((byte)(0)))));
            this.txtMessages.Location = new System.Drawing.Point(3, 3);
            this.txtMessages.Margin = new System.Windows.Forms.Padding(5, 3, 5, 3);
            this.txtMessages.MouseState = MaterialSkin.MouseState.HOVER;
            this.txtMessages.Name = "txtMessages";
            this.txtMessages.ReadOnly = true;
            this.txtMessages.Size = new System.Drawing.Size(774, 289);
            this.txtMessages.TabIndex = 1;
            this.txtMessages.Text = "";
            // 
            // chkQueue
            // 
            this.chkQueue.AutoSize = true;
            this.chkQueue.Depth = 0;
            this.chkQueue.Location = new System.Drawing.Point(6, 62);
            this.chkQueue.Margin = new System.Windows.Forms.Padding(0);
            this.chkQueue.MouseLocation = new System.Drawing.Point(-1, -1);
            this.chkQueue.MouseState = MaterialSkin.MouseState.HOVER;
            this.chkQueue.Name = "chkQueue";
            this.chkQueue.ReadOnly = false;
            this.chkQueue.Ripple = true;
            this.chkQueue.Size = new System.Drawing.Size(204, 37);
            this.chkQueue.TabIndex = 5;
            this.chkQueue.Text = "Submit queue time data";
            this.chkQueue.UseVisualStyleBackColor = true;
            // 
            // btnTestToken
            // 
            this.btnTestToken.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.btnTestToken.Density = MaterialSkin.Controls.MaterialButton.MaterialButtonDensity.Default;
            this.btnTestToken.Depth = 0;
            this.btnTestToken.HighEmphasis = true;
            this.btnTestToken.Icon = null;
            this.btnTestToken.Location = new System.Drawing.Point(410, 23);
            this.btnTestToken.Margin = new System.Windows.Forms.Padding(4, 6, 4, 6);
            this.btnTestToken.MouseState = MaterialSkin.MouseState.HOVER;
            this.btnTestToken.Name = "btnTestToken";
            this.btnTestToken.NoAccentTextColor = System.Drawing.Color.FromArgb(((int)(((byte)(63)))), ((int)(((byte)(81)))), ((int)(((byte)(181)))));
            this.btnTestToken.Size = new System.Drawing.Size(64, 36);
            this.btnTestToken.TabIndex = 4;
            this.btnTestToken.Text = "Test";
            this.btnTestToken.Type = MaterialSkin.Controls.MaterialButton.MaterialButtonType.Contained;
            this.btnTestToken.UseAccentColor = false;
            this.btnTestToken.UseVisualStyleBackColor = true;
            this.btnTestToken.Click += new System.EventHandler(this.btnTestToken_Click);
            // 
            // txtToken
            // 
            this.txtToken.AnimateReadOnly = false;
            this.txtToken.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.txtToken.Depth = 0;
            this.txtToken.Font = new System.Drawing.Font("Roboto", 16F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.txtToken.Hint = "Tarkov Tracker API Token";
            this.txtToken.LeadingIcon = null;
            this.txtToken.Location = new System.Drawing.Point(6, 9);
            this.txtToken.MaxLength = 50;
            this.txtToken.MouseState = MaterialSkin.MouseState.OUT;
            this.txtToken.Multiline = false;
            this.txtToken.Name = "txtToken";
            this.txtToken.Size = new System.Drawing.Size(325, 50);
            this.txtToken.TabIndex = 3;
            this.txtToken.Text = "";
            this.txtToken.TrailingIcon = null;
            // 
            // materialTabSelector1
            // 
            this.materialTabSelector1.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.materialTabSelector1.BaseTabControl = this.tabsMain;
            this.materialTabSelector1.CharacterCasing = MaterialSkin.Controls.MaterialTabSelector.CustomCharacterCasing.Normal;
            this.materialTabSelector1.Depth = 0;
            this.materialTabSelector1.Font = new System.Drawing.Font("Roboto", 14F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.materialTabSelector1.Location = new System.Drawing.Point(6, 67);
            this.materialTabSelector1.MouseState = MaterialSkin.MouseState.HOVER;
            this.materialTabSelector1.Name = "materialTabSelector1";
            this.materialTabSelector1.Size = new System.Drawing.Size(788, 48);
            this.materialTabSelector1.TabIndex = 3;
            this.materialTabSelector1.Text = "materialTabSelector1";
            // 
            // tabsMain
            // 
            this.tabsMain.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.tabsMain.Controls.Add(this.tabMessages);
            this.tabsMain.Controls.Add(this.tabLoadouts);
            this.tabsMain.Controls.Add(this.tabSettings);
            this.tabsMain.Controls.Add(this.tabLogs);
            this.tabsMain.Depth = 0;
            this.tabsMain.Location = new System.Drawing.Point(6, 121);
            this.tabsMain.MouseState = MaterialSkin.MouseState.HOVER;
            this.tabsMain.Multiline = true;
            this.tabsMain.Name = "tabsMain";
            this.tabsMain.SelectedIndex = 0;
            this.tabsMain.Size = new System.Drawing.Size(788, 323);
            this.tabsMain.TabIndex = 4;
            // 
            // tabMessages
            // 
            this.tabMessages.Controls.Add(this.txtMessages);
            this.tabMessages.Location = new System.Drawing.Point(4, 24);
            this.tabMessages.Name = "tabMessages";
            this.tabMessages.Padding = new System.Windows.Forms.Padding(3);
            this.tabMessages.Size = new System.Drawing.Size(780, 295);
            this.tabMessages.TabIndex = 0;
            this.tabMessages.Text = "Messages";
            this.tabMessages.UseVisualStyleBackColor = true;
            // 
            // tabLoadouts
            // 
            this.tabLoadouts.Controls.Add(this.listBoxLoadout);
            this.tabLoadouts.Controls.Add(this.comboGroupMembers);
            this.tabLoadouts.Location = new System.Drawing.Point(4, 24);
            this.tabLoadouts.Name = "tabLoadouts";
            this.tabLoadouts.Size = new System.Drawing.Size(780, 295);
            this.tabLoadouts.TabIndex = 3;
            this.tabLoadouts.Text = "Group Loadouts";
            this.tabLoadouts.UseVisualStyleBackColor = true;
            // 
            // listBoxLoadout
            // 
            this.listBoxLoadout.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.listBoxLoadout.BackColor = System.Drawing.Color.White;
            this.listBoxLoadout.BorderColor = System.Drawing.Color.LightGray;
            this.listBoxLoadout.Depth = 0;
            this.listBoxLoadout.Font = new System.Drawing.Font("Microsoft Sans Serif", 16F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.listBoxLoadout.Location = new System.Drawing.Point(3, 58);
            this.listBoxLoadout.MouseState = MaterialSkin.MouseState.HOVER;
            this.listBoxLoadout.Name = "listBoxLoadout";
            this.listBoxLoadout.SelectedIndex = -1;
            this.listBoxLoadout.SelectedItem = null;
            this.listBoxLoadout.Size = new System.Drawing.Size(774, 234);
            this.listBoxLoadout.TabIndex = 2;
            // 
            // comboGroupMembers
            // 
            this.comboGroupMembers.AutoResize = false;
            this.comboGroupMembers.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(255)))), ((int)(((byte)(255)))), ((int)(((byte)(255)))));
            this.comboGroupMembers.Depth = 0;
            this.comboGroupMembers.DrawMode = System.Windows.Forms.DrawMode.OwnerDrawVariable;
            this.comboGroupMembers.DropDownHeight = 174;
            this.comboGroupMembers.DropDownStyle = System.Windows.Forms.ComboBoxStyle.DropDownList;
            this.comboGroupMembers.DropDownWidth = 121;
            this.comboGroupMembers.Font = new System.Drawing.Font("Microsoft Sans Serif", 14F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            this.comboGroupMembers.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(222)))), ((int)(((byte)(0)))), ((int)(((byte)(0)))), ((int)(((byte)(0)))));
            this.comboGroupMembers.FormattingEnabled = true;
            this.comboGroupMembers.IntegralHeight = false;
            this.comboGroupMembers.ItemHeight = 43;
            this.comboGroupMembers.Location = new System.Drawing.Point(3, 3);
            this.comboGroupMembers.MaxDropDownItems = 4;
            this.comboGroupMembers.MouseState = MaterialSkin.MouseState.OUT;
            this.comboGroupMembers.Name = "comboGroupMembers";
            this.comboGroupMembers.Size = new System.Drawing.Size(474, 49);
            this.comboGroupMembers.StartIndex = 0;
            this.comboGroupMembers.TabIndex = 0;
            this.comboGroupMembers.SelectedIndexChanged += new System.EventHandler(this.comboGroupMembers_SelectedIndexChanged);
            // 
            // tabSettings
            // 
            this.tabSettings.Controls.Add(this.btnPlayRaidSound);
            this.tabSettings.Controls.Add(this.chkRaidStartAlert);
            this.tabSettings.Controls.Add(this.btnTarkovTrackerLink);
            this.tabSettings.Controls.Add(this.btnCancelSettings);
            this.tabSettings.Controls.Add(this.btnSaveSettings);
            this.tabSettings.Controls.Add(this.txtToken);
            this.tabSettings.Controls.Add(this.btnTestToken);
            this.tabSettings.Controls.Add(this.chkQueue);
            this.tabSettings.Location = new System.Drawing.Point(4, 24);
            this.tabSettings.Name = "tabSettings";
            this.tabSettings.Padding = new System.Windows.Forms.Padding(3);
            this.tabSettings.Size = new System.Drawing.Size(780, 295);
            this.tabSettings.TabIndex = 1;
            this.tabSettings.Text = "Settings";
            this.tabSettings.UseVisualStyleBackColor = true;
            // 
            // btnPlayRaidSound
            // 
            this.btnPlayRaidSound.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.btnPlayRaidSound.Density = MaterialSkin.Controls.MaterialButton.MaterialButtonDensity.Default;
            this.btnPlayRaidSound.Depth = 0;
            this.btnPlayRaidSound.HighEmphasis = true;
            this.btnPlayRaidSound.Icon = null;
            this.btnPlayRaidSound.Location = new System.Drawing.Point(210, 100);
            this.btnPlayRaidSound.Margin = new System.Windows.Forms.Padding(4, 6, 4, 6);
            this.btnPlayRaidSound.MouseState = MaterialSkin.MouseState.HOVER;
            this.btnPlayRaidSound.Name = "btnPlayRaidSound";
            this.btnPlayRaidSound.NoAccentTextColor = System.Drawing.Color.Empty;
            this.btnPlayRaidSound.Size = new System.Drawing.Size(64, 36);
            this.btnPlayRaidSound.TabIndex = 10;
            this.btnPlayRaidSound.Text = "🔊";
            this.btnPlayRaidSound.Type = MaterialSkin.Controls.MaterialButton.MaterialButtonType.Contained;
            this.btnPlayRaidSound.UseAccentColor = false;
            this.btnPlayRaidSound.UseVisualStyleBackColor = true;
            this.btnPlayRaidSound.Click += new System.EventHandler(this.btnPlayRaidSound_Click);
            // 
            // chkRaidStartAlert
            // 
            this.chkRaidStartAlert.AutoSize = true;
            this.chkRaidStartAlert.Depth = 0;
            this.chkRaidStartAlert.Location = new System.Drawing.Point(6, 99);
            this.chkRaidStartAlert.Margin = new System.Windows.Forms.Padding(0);
            this.chkRaidStartAlert.MouseLocation = new System.Drawing.Point(-1, -1);
            this.chkRaidStartAlert.MouseState = MaterialSkin.MouseState.HOVER;
            this.chkRaidStartAlert.Name = "chkRaidStartAlert";
            this.chkRaidStartAlert.ReadOnly = false;
            this.chkRaidStartAlert.Ripple = true;
            this.chkRaidStartAlert.Size = new System.Drawing.Size(200, 37);
            this.chkRaidStartAlert.TabIndex = 9;
            this.chkRaidStartAlert.Text = "Audio alert on raid start";
            this.chkRaidStartAlert.UseVisualStyleBackColor = true;
            // 
            // btnTarkovTrackerLink
            // 
            this.btnTarkovTrackerLink.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.btnTarkovTrackerLink.Density = MaterialSkin.Controls.MaterialButton.MaterialButtonDensity.Default;
            this.btnTarkovTrackerLink.Depth = 0;
            this.btnTarkovTrackerLink.HighEmphasis = true;
            this.btnTarkovTrackerLink.Icon = null;
            this.btnTarkovTrackerLink.Location = new System.Drawing.Point(338, 23);
            this.btnTarkovTrackerLink.Margin = new System.Windows.Forms.Padding(4, 6, 4, 6);
            this.btnTarkovTrackerLink.MouseState = MaterialSkin.MouseState.HOVER;
            this.btnTarkovTrackerLink.Name = "btnTarkovTrackerLink";
            this.btnTarkovTrackerLink.NoAccentTextColor = System.Drawing.Color.Empty;
            this.btnTarkovTrackerLink.Size = new System.Drawing.Size(64, 36);
            this.btnTarkovTrackerLink.TabIndex = 8;
            this.btnTarkovTrackerLink.Text = "🔗";
            this.btnTarkovTrackerLink.Type = MaterialSkin.Controls.MaterialButton.MaterialButtonType.Contained;
            this.btnTarkovTrackerLink.UseAccentColor = false;
            this.btnTarkovTrackerLink.UseVisualStyleBackColor = true;
            this.btnTarkovTrackerLink.Click += new System.EventHandler(this.btnTarkovTrackerLink_Click);
            // 
            // btnCancelSettings
            // 
            this.btnCancelSettings.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.btnCancelSettings.Density = MaterialSkin.Controls.MaterialButton.MaterialButtonDensity.Default;
            this.btnCancelSettings.Depth = 0;
            this.btnCancelSettings.HighEmphasis = true;
            this.btnCancelSettings.Icon = null;
            this.btnCancelSettings.Location = new System.Drawing.Point(79, 142);
            this.btnCancelSettings.Margin = new System.Windows.Forms.Padding(4, 6, 4, 6);
            this.btnCancelSettings.MouseState = MaterialSkin.MouseState.HOVER;
            this.btnCancelSettings.Name = "btnCancelSettings";
            this.btnCancelSettings.NoAccentTextColor = System.Drawing.Color.Empty;
            this.btnCancelSettings.Size = new System.Drawing.Size(75, 36);
            this.btnCancelSettings.TabIndex = 7;
            this.btnCancelSettings.Text = "Revert";
            this.btnCancelSettings.Type = MaterialSkin.Controls.MaterialButton.MaterialButtonType.Contained;
            this.btnCancelSettings.UseAccentColor = false;
            this.btnCancelSettings.UseVisualStyleBackColor = true;
            this.btnCancelSettings.Click += new System.EventHandler(this.panelSettings_CancelClick);
            // 
            // btnSaveSettings
            // 
            this.btnSaveSettings.AutoSizeMode = System.Windows.Forms.AutoSizeMode.GrowAndShrink;
            this.btnSaveSettings.Density = MaterialSkin.Controls.MaterialButton.MaterialButtonDensity.Default;
            this.btnSaveSettings.Depth = 0;
            this.btnSaveSettings.HighEmphasis = true;
            this.btnSaveSettings.Icon = null;
            this.btnSaveSettings.Location = new System.Drawing.Point(7, 142);
            this.btnSaveSettings.Margin = new System.Windows.Forms.Padding(4, 6, 4, 6);
            this.btnSaveSettings.MouseState = MaterialSkin.MouseState.HOVER;
            this.btnSaveSettings.Name = "btnSaveSettings";
            this.btnSaveSettings.NoAccentTextColor = System.Drawing.Color.Empty;
            this.btnSaveSettings.Size = new System.Drawing.Size(64, 36);
            this.btnSaveSettings.TabIndex = 6;
            this.btnSaveSettings.Text = "Save";
            this.btnSaveSettings.Type = MaterialSkin.Controls.MaterialButton.MaterialButtonType.Contained;
            this.btnSaveSettings.UseAccentColor = false;
            this.btnSaveSettings.UseVisualStyleBackColor = true;
            this.btnSaveSettings.Click += new System.EventHandler(this.panelSettings_SaveClick);
            // 
            // tabLogs
            // 
            this.tabLogs.Controls.Add(this.txtLogs);
            this.tabLogs.Location = new System.Drawing.Point(4, 24);
            this.tabLogs.Name = "tabLogs";
            this.tabLogs.Size = new System.Drawing.Size(780, 295);
            this.tabLogs.TabIndex = 2;
            this.tabLogs.Text = "Logs Output";
            this.tabLogs.UseVisualStyleBackColor = true;
            // 
            // txtLogs
            // 
            this.txtLogs.BackColor = System.Drawing.Color.FromArgb(((int)(((byte)(255)))), ((int)(((byte)(255)))), ((int)(((byte)(255)))));
            this.txtLogs.BorderStyle = System.Windows.Forms.BorderStyle.None;
            this.txtLogs.Depth = 0;
            this.txtLogs.Dock = System.Windows.Forms.DockStyle.Fill;
            this.txtLogs.Font = new System.Drawing.Font("Microsoft Sans Serif", 14F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.txtLogs.ForeColor = System.Drawing.Color.FromArgb(((int)(((byte)(222)))), ((int)(((byte)(0)))), ((int)(((byte)(0)))), ((int)(((byte)(0)))));
            this.txtLogs.Location = new System.Drawing.Point(0, 0);
            this.txtLogs.MouseState = MaterialSkin.MouseState.HOVER;
            this.txtLogs.Name = "txtLogs";
            this.txtLogs.ReadOnly = true;
            this.txtLogs.Size = new System.Drawing.Size(780, 295);
            this.txtLogs.TabIndex = 0;
            this.txtLogs.Text = "";
            // 
            // MainWindow
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.AutoSize = true;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.tabsMain);
            this.Controls.Add(this.materialTabSelector1);
            this.Name = "MainWindow";
            this.Text = "Tarkov Monitor";
            this.tabsMain.ResumeLayout(false);
            this.tabMessages.ResumeLayout(false);
            this.tabLoadouts.ResumeLayout(false);
            this.tabSettings.ResumeLayout(false);
            this.tabSettings.PerformLayout();
            this.tabLogs.ResumeLayout(false);
            this.ResumeLayout(false);

        }

        #endregion

        private MaterialSkin.Controls.MaterialMultiLineTextBox txtMessages;
        private MaterialSkin.Controls.MaterialButton btnTestToken;
        private MaterialSkin.Controls.MaterialTextBox txtToken;
        private MaterialSkin.Controls.MaterialCheckbox chkQueue;
        private MaterialSkin.Controls.MaterialTabSelector materialTabSelector1;
        private MaterialSkin.Controls.MaterialTabControl tabsMain;
        private TabPage tabMessages;
        private TabPage tabSettings;
        private MaterialSkin.Controls.MaterialButton btnCancelSettings;
        private MaterialSkin.Controls.MaterialButton btnSaveSettings;
        private TabPage tabLogs;
        private MaterialSkin.Controls.MaterialMultiLineTextBox txtLogs;
        private MaterialSkin.Controls.MaterialButton btnTarkovTrackerLink;
        private MaterialSkin.Controls.MaterialCheckbox chkRaidStartAlert;
        private MaterialSkin.Controls.MaterialButton btnPlayRaidSound;
        private TabPage tabLoadouts;
        private MaterialSkin.Controls.MaterialComboBox comboGroupMembers;
        private MaterialSkin.Controls.MaterialListBox listBoxLoadout;
    }
}