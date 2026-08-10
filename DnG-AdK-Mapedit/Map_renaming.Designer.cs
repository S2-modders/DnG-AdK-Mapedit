namespace DnG_AdK_Mapedit
{
    partial class Map_renaming
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
            System.ComponentModel.ComponentResourceManager resources = new System.ComponentModel.ComponentResourceManager(typeof(Map_renaming));
            this.Map_name_edit = new System.Windows.Forms.TextBox();
            this.Accept_button = new System.Windows.Forms.Button();
            this.Cancel_button = new System.Windows.Forms.Button();
            this.SuspendLayout();
            // 
            // Map_name_edit
            // 
            this.Map_name_edit.Location = new System.Drawing.Point(12, 12);
            this.Map_name_edit.Name = "Map_name_edit";
            this.Map_name_edit.Size = new System.Drawing.Size(332, 36);
            this.Map_name_edit.TabIndex = 0;
            // 
            // Accept_button
            // 
            this.Accept_button.AutoSize = true;
            this.Accept_button.Location = new System.Drawing.Point(12, 59);
            this.Accept_button.Name = "Accept_button";
            this.Accept_button.Size = new System.Drawing.Size(160, 40);
            this.Accept_button.TabIndex = 1;
            this.Accept_button.Text = "Accept";
            this.Accept_button.UseVisualStyleBackColor = true;
            this.Accept_button.Click += new System.EventHandler(this.Accept_button_Click);
            // 
            // Cancel_button
            // 
            this.Cancel_button.AutoSize = true;
            this.Cancel_button.Location = new System.Drawing.Point(184, 59);
            this.Cancel_button.Name = "Cancel_button";
            this.Cancel_button.Size = new System.Drawing.Size(160, 40);
            this.Cancel_button.TabIndex = 2;
            this.Cancel_button.Text = "Cancel";
            this.Cancel_button.UseVisualStyleBackColor = true;
            this.Cancel_button.Click += new System.EventHandler(this.Cancel_button_Click);
            // 
            // Map_renaming
            // 
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.None;
            this.ClientSize = new System.Drawing.Size(356, 111);
            this.Controls.Add(this.Cancel_button);
            this.Controls.Add(this.Accept_button);
            this.Controls.Add(this.Map_name_edit);
            this.Font = new System.Drawing.Font("Segoe UI Variable Text", 9.142858F);
            this.FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedSingle;
            this.Icon = ((System.Drawing.Icon)(resources.GetObject("$this.Icon")));
            this.MaximizeBox = false;
            this.Name = "Map_renaming";
            this.ShowIcon = false;
            this.ShowInTaskbar = false;
            this.StartPosition = System.Windows.Forms.FormStartPosition.CenterParent;
            this.Text = "Rename map";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private System.Windows.Forms.TextBox Map_name_edit;
        private System.Windows.Forms.Button Accept_button;
        private System.Windows.Forms.Button Cancel_button;
    }
}