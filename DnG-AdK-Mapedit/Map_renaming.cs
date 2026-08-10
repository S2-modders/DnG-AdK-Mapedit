using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace DnG_AdK_Mapedit
{
    public partial class Map_renaming : Form
    {
        int Old_map_name_length = 0;

        public string Map_name
        {
            get => Map_name_edit.Text;
            set => Map_name_edit.Text = value;
        }

        public Map_renaming(string currentMapName)
        {
            InitializeComponent();
            Map_name_edit.Text = currentMapName;
            Old_map_name_length = currentMapName.Length;
        }

        private void Accept_button_Click(object sender, EventArgs e)
        {
            if (Map_name_edit.Text.Length <= 20)
            {
                if (Map_name_edit.Text.Length > 0)
                {
                    this.DialogResult = DialogResult.OK;
                    this.Close();
                }
                else
                {
                    MessageBox.Show("Map name cannot be empty.", "Map name not provided", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            else
            {
                MessageBox.Show("Current map name with a length of " + Map_name_edit.Text.Length + " characters is larger than the maximum allowed of 20 characters", "Map name is too long", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void Cancel_button_Click(object sender, EventArgs e)
        {
            if (Old_map_name_length <= 20)
            {
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            }
            else
            {
                // Allow the user to keep the old map name even if it exceeds the maximum length, but warn them about it.
                MessageBox.Show("Old map name with a length of " + Old_map_name_length + " characters is larger than the maximum allowed of 20 characters", "Old map name is too long", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                this.DialogResult = DialogResult.Cancel;
                this.Close();
            }
        }
    }
}
