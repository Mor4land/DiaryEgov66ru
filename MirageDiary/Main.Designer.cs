namespace MirageDiary
{
    partial class Main
    {
        /// <summary>
        /// Обязательная переменная конструктора.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        /// <summary>
        /// Освободить все используемые ресурсы.
        /// </summary>
        /// <param name="disposing">истинно, если управляемый ресурс должен быть удален; иначе ложно.</param>
        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Код, автоматически созданный конструктором форм Windows

        /// <summary>
        /// Требуемый метод для поддержки конструктора — не изменяйте 
        /// содержимое этого метода с помощью редактора кода.
        /// </summary>
        private void InitializeComponent()
        {
            this.webBrowser1 = new System.Windows.Forms.WebBrowser();
            this.webView21 = new Microsoft.Web.WebView2.WinForms.WebView2();
            this.panelBottom = new System.Windows.Forms.Panel();
            this.lblChatId = new System.Windows.Forms.Label();
            this.txtChatId = new System.Windows.Forms.TextBox();
            this.btnSync = new System.Windows.Forms.Button();
            ((System.ComponentModel.ISupportInitialize)(this.webView21)).BeginInit();
            this.panelBottom.SuspendLayout();
            this.SuspendLayout();
            // 
            // webBrowser1
            // 
            this.webBrowser1.Dock = System.Windows.Forms.DockStyle.Fill;
            this.webBrowser1.Location = new System.Drawing.Point(0, 0);
            this.webBrowser1.MinimumSize = new System.Drawing.Size(20, 20);
            this.webBrowser1.Name = "webBrowser1";
            this.webBrowser1.Size = new System.Drawing.Size(800, 450);
            this.webBrowser1.TabIndex = 0;
            // 
            // webView21
            // 
            this.webView21.AllowExternalDrop = true;
            this.webView21.CreationProperties = null;
            this.webView21.DefaultBackgroundColor = System.Drawing.Color.White;
            this.webView21.Dock = System.Windows.Forms.DockStyle.Fill;
            this.webView21.Location = new System.Drawing.Point(0, 0);
            this.webView21.Name = "webView21";
            this.webView21.Size = new System.Drawing.Size(800, 405);
            this.webView21.TabIndex = 1;
            this.webView21.ZoomFactor = 1D;
            // 
            // panelBottom
            // 
            this.panelBottom.Controls.Add(this.lblChatId);
            this.panelBottom.Controls.Add(this.txtChatId);
            this.panelBottom.Controls.Add(this.btnSync);
            this.panelBottom.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.panelBottom.Location = new System.Drawing.Point(0, 405);
            this.panelBottom.Name = "panelBottom";
            this.panelBottom.Size = new System.Drawing.Size(800, 45);
            this.panelBottom.TabIndex = 2;
            // 
            // lblChatId
            // 
            this.lblChatId.AutoSize = true;
            this.lblChatId.Location = new System.Drawing.Point(12, 16);
            this.lblChatId.Name = "lblChatId";
            this.lblChatId.Size = new System.Drawing.Size(95, 13);
            this.lblChatId.TabIndex = 0;
            this.lblChatId.Text = "Telegram Chat ID:";
            // 
            // txtChatId
            // 
            this.txtChatId.Location = new System.Drawing.Point(113, 13);
            this.txtChatId.Name = "txtChatId";
            this.txtChatId.Size = new System.Drawing.Size(150, 20);
            this.txtChatId.TabIndex = 1;
            // 
            // btnSync
            // 
            this.btnSync.Location = new System.Drawing.Point(269, 11);
            this.btnSync.Name = "btnSync";
            this.btnSync.Size = new System.Drawing.Size(140, 23);
            this.btnSync.TabIndex = 2;
            this.btnSync.Text = "Связать с Telegram";
            this.btnSync.UseVisualStyleBackColor = true;
            this.btnSync.Click += new System.EventHandler(this.btnSync_Click);
            // 
            // Main
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(6F, 13F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(800, 450);
            this.Controls.Add(this.webView21);
            this.Controls.Add(this.panelBottom);
            this.Controls.Add(this.webBrowser1);
            this.Name = "Main";
            this.Text = "Mirage Diary";
            this.Load += new System.EventHandler(this.Form1_Load);
            ((System.ComponentModel.ISupportInitialize)(this.webView21)).EndInit();
            this.panelBottom.ResumeLayout(false);
            this.panelBottom.PerformLayout();
            this.ResumeLayout(false);

        }

        #endregion

        private System.Windows.Forms.WebBrowser webBrowser1;
        private Microsoft.Web.WebView2.WinForms.WebView2 webView21;
        private System.Windows.Forms.Panel panelBottom;
        private System.Windows.Forms.Label lblChatId;
        private System.Windows.Forms.TextBox txtChatId;
        private System.Windows.Forms.Button btnSync;
    }
}
