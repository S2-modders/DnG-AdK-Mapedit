using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using File = System.IO.File;

namespace DnG_AdK_Mapedit
{
    public partial class DnG_AdK_Mapedit : Form
    {
        string TempFolder = Path.Combine(Path.GetTempPath(), "DnG-AdK-Mapedit/");
        private string WorkingFileName => Path.Combine(TempFolder, "temp_" + Path.GetFileName(DnG_map_path.Text));
        private string archiverPath => Path.Combine(TempFolder, "decryptor_s2.exe");

        // Keep a reference to the external process so it is not GC-collected
        private Process archiverProcess;
        //Archiver data
        private bool DnG;
        private bool Compress;
        private string sourceFileName;
        private string destinationFileName;

        private int Player_count;

        private int Map_size_x;
        private int Map_size_y;

        private static readonly byte[] HeightsHeader = { 0x01, 0x00, 0x00, 0x00, 0x71, 0x28, 0x0B, 0x82, 0x0C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x9C, 0xFF, 0xFF, 0xFF };
        private static readonly byte[] TexturesHeader = { 0x00, 0x00, 0x00, 0x00, 0xB4, 0x88, 0xC8, 0x75, 0x0A, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
        private static readonly byte[] Resources_header = { 0x00, 0x00, 0x00, 0x00, 0xB0, 0xBB, 0xC3, 0x7C, 0x0D, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };

        private const uint CoalHex = 0x7068DCD3;      // Little-endian values
        private const uint IronHex = 0xEC5020BE;
        private const uint SaltHex = 0x09D2D623;
        private const uint GoldHex = 0x4F41C633;
        private const uint GemstonesHex = 0xCB98C903;
        private const uint StoneHex = 0x55E952D3;
        private const uint FishHex = 0x4012E5A3;
        private const uint WaterHex = 0xA9676263;

        private const uint EmptyHex = 0xFFFFFFFF;

        private List<(int tab, int from, int to)> Swap_list = new List<(int, int, int)>();

        private List<(int pos_x, int pos_y, int rotation, bool anchorage, int anchor_x, int anchor_y, int buoy_1_connection, int buoy_2_connection)> Harbours_list = new List<(int, int, int, bool, int, int, int, int)>();
        // Flag to prevent UI updates from triggering save events
        private bool isUpdatingUI = false;

        private List<(int pos_x, int pos_y, int type)> Caves_list = new List<(int, int, int)>();

        public DnG_AdK_Mapedit()
        {
            //Uncomment to test multi-language support
            //Thread.CurrentThread.CurrentUICulture = new CultureInfo("pl-PL");
            //Thread.CurrentThread.CurrentCulture = new CultureInfo("pl-PL");

            Directory.CreateDirectory(TempFolder);

            InitializeComponent();
        }

        private void Changelog_button_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            string message = @"Changes compared to the original map converter:

• Invalid resources are now automatically removed
• Swapping was added to allow using new assets
• Harbour code was added but currently it's causing game crashes
• Caves section now works properly
• Knowledge of exact sacrifice names is not required as icons are displayed instead
• Sacrifice limits are now automatically checked and displayed
• Each sacrifice preset is now stored in individual files and can be easily exported
• Default player colours can now be customized
• Whole map preset can be now saved not requiring inputting values manually with each map edit
• Support for maps with odd player counts was added
• Maps no longer crash randomly during gameplay";

            MessageBox.Show(message, "Changelog", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void DnG_AdK_mapedit_Load(object sender, System.EventArgs e)
        {
            //For now disable broken harbour section
            Harbours_tab.Enabled = false;

            Resources_wait.Visible = false;
            Export_wait.Visible = false;
            Tab_control.Enabled = false;

            Harbour_panel.Enabled = false;
            Harbour_anchor_panel.Enabled = false;
            Cave_panel.Enabled = false;

            Sacrifice_included_presets.SelectedIndex = 0;
            Sacrifice_included_presets.Enabled = false;
        }

        //User interacts with the DnG map file path textbox
        private void DnG_map_path_MouseDown(object sender, MouseEventArgs e)
        {
            using (OpenFileDialog openFileDialog = new OpenFileDialog())
            {
                openFileDialog.Filter = "DnG map file (*.s2m)|*.s2m|All files (*.*)|*.*";
                openFileDialog.Title = "Select a DnG map file";
                if (openFileDialog.ShowDialog() == DialogResult.OK)
                {
                    DnG_map_path.Text = openFileDialog.FileName;
                    FileValidation();
                }
            }
        }

        private void DnG_map_load_Click(object sender, EventArgs e)
        {
            if (DnG_map_path.Text == "")
            {
                MessageBox.Show("Please select a DnG map file first.", "No file selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            if (!File.Exists(DnG_map_path.Text))
            {
                MessageBox.Show("The selected file does not exist.", "File not found", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            FileValidation();
        }

        //Determining if the selected file is a DnG map
        void FileValidation()
        {
            byte[] DnG_map = File.ReadAllBytes(DnG_map_path.Text);
            byte[] File_header = DnG_map.Take(8).ToArray();
            //Only compressed
            byte[] DnG_header = { 0x12, 0x18, 0x09, 0x06, 0x72, 0x63, 0x30, 0x30 };

            //If the file is a DnG map it can be decompressed with an external executable
            if (File_header.SequenceEqual(DnG_header))
            {
                DnG = true;
                Compress = false;
                sourceFileName = DnG_map_path.Text;
                destinationFileName = WorkingFileName;

                Archiver();
                return;
            }

            //Only compressed
            byte[] SAdK_header = { 0x12, 0x18, 0x09, 0x06, 0x73, 0x61, 0x64, 0x6B };

            if (File_header.SequenceEqual(SAdK_header))
            {
                MessageBox.Show("Already exported maps can't be edited.", "Invalid file", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                MessageBox.Show("This is not a valid DnG map file.", "Invalid file", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        //Using the external executable to make the file readable
        void Archiver()
        {
            //Unpack the archiver executable to the temp path
            if (!File.Exists(archiverPath))
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream("DnG_AdK_Mapedit.decryptor_s2.exe"))
                {
                    using (FileStream fileStream = new FileStream(archiverPath, FileMode.Create, FileAccess.Write))
                    {
                        stream.CopyTo(fileStream);
                    }
                }
            }

            // Many programs that accept files dragged onto their icon simply receive the
            // dropped file path as the first command-line argument. Replicate that by
            // starting the decryptor with the map filename as an argument. Quote the
            // argument to handle spaces in paths and set the working directory so the
            // decryptor sees the copied file in its current folder.
            // Store the process in a field so the GC won't collect it before it exits
            archiverProcess = new Process();
            archiverProcess.StartInfo.FileName = archiverPath;

            if (Compress)
            {
                if (DnG)
                {
                    string tempFileName = Path.Combine(
                    Path.GetDirectoryName(sourceFileName) ?? "",
                    Path.GetFileNameWithoutExtension(sourceFileName) + ".dng.s2m"
                    );

                    Tab_control.Enabled = false;
                    Resources_wait.Visible = true;
                    File.Move(sourceFileName, tempFileName);
                    archiverProcess.StartInfo.Arguments = "\"" + tempFileName + "\""; // quoted
                }
                else
                {
                    string tempFileName = Path.Combine(
                    Path.GetDirectoryName(sourceFileName) ?? "",
                    Path.GetFileNameWithoutExtension(sourceFileName) + ".adk.s2m"
                    );

                    File.Move(sourceFileName, tempFileName);
                    archiverProcess.StartInfo.Arguments = "\"" + tempFileName + "\""; // quoted
                }
            }
            else
            {
                if (File.Exists(destinationFileName))
                {
                    File.Delete(destinationFileName);
                }
                File.Copy(sourceFileName, destinationFileName);
                archiverProcess.StartInfo.Arguments = "\"" + destinationFileName + "\""; // quoted
            }

            archiverProcess.StartInfo.WorkingDirectory = Application.StartupPath;
            archiverProcess.StartInfo.UseShellExecute = false;
            archiverProcess.StartInfo.RedirectStandardOutput = true;
            archiverProcess.StartInfo.RedirectStandardError = true;
            archiverProcess.EnableRaisingEvents = true;
            archiverProcess.Exited += ExternalApp_Exited;

            try
            {
                // Start the process. We do not block here; ExternalApp_Exited will run
                // when the process exits and check for the output file.
                archiverProcess.Start();
                // Optionally begin reading output/errors so the process doesn't block
                // if it writes large amounts to stdout/stderr.
                archiverProcess.BeginOutputReadLine();
                archiverProcess.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to start decompressor: " + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        //Deleting a temporary files when the app is closed
        private void DnG_AdK_Mapedit_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (File.Exists(WorkingFileName))
            {
                File.Delete(WorkingFileName);
            }
            if (File.Exists(archiverPath))
            {
                File.Delete(archiverPath);
            }
        }

        //Archiver process exit
        private void ExternalApp_Exited(object sender, EventArgs e)
        {
            this.Invoke((MethodInvoker)delegate
            {
                if (Compress)
                {
                    if (File.Exists(sourceFileName))
                    {

                        if (DnG)
                        {
                            //Moving the compressed file to a target location
                            if (File.Exists(destinationFileName))
                            {
                                File.Delete(destinationFileName);
                            }
                            File.Move(sourceFileName, destinationFileName);

                            //Recover uncompressed file
                            string tempFileName = Path.Combine(
                            Path.GetDirectoryName(sourceFileName) ?? "",
                            Path.GetFileNameWithoutExtension(sourceFileName) + ".dng.s2m"
                            );
                            File.Move(tempFileName, sourceFileName);

                            Tab_control.Enabled = true;
                            Resources_wait.Visible = false;
                        }
                        else
                        {
                            //Moving the compressed file to a target location
                            if (File.Exists(destinationFileName))
                            {
                                File.Delete(destinationFileName);
                            }
                            File.Move(sourceFileName, destinationFileName);

                            
                            //Clean up the uncompressed temporary export file
                            string tempFileName = Path.Combine(
                                Path.GetDirectoryName(sourceFileName) ?? "",
                                Path.GetFileNameWithoutExtension(sourceFileName) + ".adk.s2m"
                            );

                            if (File.Exists(tempFileName))
                            {
                                File.Delete(tempFileName);
                            }

                            //Copy the prieview render
                            if (Map_preview_checkbox.Checked)
                            {
                                string preview_source = Path.ChangeExtension(DnG_map_path.Text, ".bmp");
                                string preview_destination = Path.ChangeExtension(destinationFileName, ".bmp");

                                if (File.Exists(preview_source))
                                {
                                    // Prevent copying if the source and destination are the exact same file
                                    if (!string.Equals(preview_source, preview_destination, StringComparison.OrdinalIgnoreCase))
                                    {
                                        File.Copy(preview_source, preview_destination, true);
                                    }
                                }
                            }

                            // Re-enable UI
                            Tab_control.Enabled = true;
                            Export_wait.Visible = false;
                        }
                    }
                    else
                    {
                        ArchiverProcessFailed();
                    }

                }
                else
                {
                    string tempFile = Path.ChangeExtension(WorkingFileName, ".dng.s2m");
                    if (File.Exists(tempFile))
                    {
                        File.Delete(destinationFileName);
                        File.Move(tempFile, destinationFileName);
                        DnG_map_path.Enabled = false;
                        DnG_map_load.Enabled = false;
                        FillMapInfo();
                    }
                    else
                    {
                        ArchiverProcessFailed();
                        return;
                    }
                }
            });
        }

        void ArchiverProcessFailed()
        {
            MessageBox.Show("Archiver process ended without output file.", "Archiver error", MessageBoxButtons.OK, MessageBoxIcon.Error);

            if (Compress)
            {
                if (DnG)
                {
                    //Recover uncompressed file
                    string tempFileName = Path.Combine(
                    Path.GetDirectoryName(sourceFileName) ?? "",
                    Path.GetFileNameWithoutExtension(sourceFileName) + ".dng.s2m"
                    );

                    File.Move(tempFileName, sourceFileName);

                    Tab_control.Enabled = true;
                    Resources_wait.Visible = false;
                }
                else
                {
                    //Recovering uncompressed file is not important
                    string tempFileName = Path.Combine(
                    Path.GetDirectoryName(sourceFileName) ?? "",
                    Path.GetFileNameWithoutExtension(sourceFileName) + ".adk.s2m"
                    );

                    File.Delete(tempFileName);

                    Tab_control.Enabled = true;
                    Export_wait.Visible = false;
                }
            }
            else
            {
                File.Delete(destinationFileName);
            }
        }

        void FillMapInfo()
        {
            //Copy the prieview render
            string bmpPath = DnG_map_path.Text.Replace(".s2m", ".bmp");
            if (File.Exists(bmpPath))
            {
                using (var stream = new FileStream(bmpPath, FileMode.Open, FileAccess.Read))
                {
                    Map_preview.BackgroundImage = Image.FromStream(stream);
                }
            }
            else
            {
                Map_preview_checkbox.Enabled = false;
            }

            byte[] DnG_map = File.ReadAllBytes(WorkingFileName);
            int current_byte = 0;
            //Skip the header
            current_byte += 12;
            //Read player count
            Player_count = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            Player_count_text.Text = "Player count: " + Player_count.ToString();
            if (Player_count < 2)
            {
                Multiplayer_prefix_checkbox.Enabled = false;
            }
            current_byte += 4;

            //00 00 00 00 -> blue
            //01 00 00 00 -> red
            //02 00 00 00 -> green
            //03 00 00 00 -> yellow
            //04 00 00 00 -> white
            //05 00 00 00 -> black
            //06 00 00 00 -> pink
            //07 00 00 00 -> light blue

            // 1. Group controls into an array for easy iteration
            var selectors = new[]
            {
                Player_1_select,
                Player_2_select,
                Player_3_select,
                Player_4_select,
                Player_5_select,
                Player_6_select
            };

            // 2. Map default color indices per player count (0-indexed: row 0 = 1 player)
            int[][] presets = new int[][]
            {
                new[] { 0 },                  // 1 player
                new[] { 0, 1 },               // 2 players
                new[] { 0, 2, 3 },            // 3 players
                new[] { 0, 2, 3, 1 },         // 4 players
                new[] { 0, 2, 3, 1, 6 },      // 5 players
                new[] { 0, 2, 3, 5, 1, 4 }    // 6 players
            };

            // 3. Apply settings cleanly with a single loop
            if (Player_count >= 1 && Player_count <= selectors.Length)
            {
                int[] activePreset = presets[Player_count - 1];

                for (int i = 0; i < selectors.Length; i++)
                {
                    bool isActive = i < Player_count;
                    selectors[i].Enabled = isActive;

                    if (isActive)
                    {
                        selectors[i].SelectedIndex = activePreset[i];
                    }
                }
            }

            //Skip start positions
            current_byte += 20 * Player_count;
            //Read map name
            int Map_name_length = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            current_byte += 4;
            string Map_name = System.Text.Encoding.UTF8.GetString(DnG_map, current_byte, Map_name_length);
            Map_name_button.Text = Map_name.ToString();
            current_byte += Map_name_length;
            //Read map size
            Map_size_x = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            current_byte += 4;
            Map_size_y = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            current_byte += 4;
            Map_info_size.Text = "Map size: " + Map_size_x.ToString() + "x" + Map_size_y.ToString();

            //Update maximum positions
            Harbour_position_X_input.Maximum = Map_size_x;
            Anchor_position_X_input.Maximum = Map_size_x;
            Cave_position_X_input.Maximum = Map_size_x;

            Harbour_position_Y_input.Maximum = Map_size_y;
            Anchor_position_Y_input.Maximum = Map_size_y;
            Cave_position_Y_input.Maximum = Map_size_y;

            UpdateResources(current_byte, DnG_map);
        }

        void UpdateResources(int current_byte, byte[] DnG_map)
        {

            byte[] Empty_hex_extended = { 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF };

            //Finding the heights array header in the map file
            current_byte = FindSequenceOffset(DnG_map, HeightsHeader, current_byte);

            if (current_byte == -1)
            {
                MessageBox.Show("Heights array not found in the map file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            //Skip map size
            int Heights_array_beginning = current_byte + 8;

            //Finding the textures array header in the map file
            current_byte = FindSequenceOffset(DnG_map, TexturesHeader, current_byte);


            if (current_byte == -1)
            {
                MessageBox.Show("Textures array not found in the map file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            //Skip array length (Map_size_x * Map_size_y)
            int Textures_array_beginning = current_byte + 4;

            //Finding the resource array header in the map file
            current_byte = FindSequenceOffset(DnG_map, Resources_header, current_byte);

            if (current_byte == -1)
            {
                MessageBox.Show("Resource array not found in the map file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            //Skip map size
            current_byte += 8;
            int Resource_array_length = Map_size_x * Map_size_y;

            int Coal_count = 0, Iron_count = 0, Salt_count = 0;
            int Gold_count = 0, Gemstones_count = 0, Stone_count = 0;

            for (int j = 0; j < Resource_array_length; j++)
            {
                // Safety check to prevent IndexOutOfRangeException
                if (current_byte + 8 > DnG_map.Length)
                {
                    MessageBox.Show("Unexpected end of file reached.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    break;
                }

                // Check if the resource amount is greater than 0
                if ((int)BitConverter.ToUInt32(DnG_map, current_byte) > 0)
                {
                    current_byte += 4; // Skip the resource amount

                    // Skip empty resources
                    if (EmptyHex == (uint)BitConverter.ToUInt32(DnG_map, current_byte))
                    {
                        current_byte += 4;
                    }
                    /*
                    //Map editor fails to remove invalid resources that are under the water.
                    //That causes the resource count to be different.
                    //Remove invalid resources that are under the water
                    else if (!IsOnLand(DnG_map, Heights_array_beginning, j, Map_size_x))
                    {
                        // Overwrite the resource amount (4 bytes) and type (4 bytes) for the current entry.
                        Array.Copy(Empty_hex_extended, 0, DnG_map, current_byte - 4, Empty_hex_extended.Length);
                        current_byte += 4; // move past the type to the next resource's amount
                    }
                    */
                    //Remove invalid resources that are not on rock textures
                    else if (!IsRockTexture(DnG_map, j, Textures_array_beginning))
                    {
                        uint resourceType = BitConverter.ToUInt32(DnG_map, current_byte);
                        if (resourceType != FishHex)
                        {
                            //Clear both amount and type for this resource entry.
                            Array.Copy(Empty_hex_extended, 0, DnG_map, current_byte - 4, Empty_hex_extended.Length);
                        }
                        current_byte += 4;
                    }
                    else
                    {
                        uint resourceType = BitConverter.ToUInt32(DnG_map, current_byte);

                        switch (resourceType)
                        {
                            case CoalHex: Coal_count++; break;
                            case IronHex: Iron_count++; break;
                            case SaltHex: Salt_count++; break;
                            case GoldHex: Gold_count++; break;
                            case GemstonesHex: Gemstones_count++; break;
                            case StoneHex: Stone_count++; break;
                            case FishHex: /* Skip */ break;
                            // Remove unused water resource: clear both amount and type for this entry.
                            case WaterHex:
                                Array.Copy(Empty_hex_extended, 0, DnG_map, current_byte - 4, 8);
                                break;
                            default:
                                MessageBox.Show($"Unknown resource type found at byte offset {current_byte} with a hex value of {BitConverter.ToUInt32(DnG_map, current_byte):X8}", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                break;
                        }

                        current_byte += 4; // Move to the next resource's amount
                    }
                }
                else
                {
                    current_byte += 8; // Skip the resource amount and the resource type
                }
            }

            int Total_resources = Coal_count + Iron_count + Salt_count + Gold_count + Gemstones_count + Stone_count;

            SetResourceUI(Coal_amount, Coal_share, Coal_count, Total_resources);
            SetResourceUI(Iron_amount, Iron_share, Iron_count, Total_resources);
            SetResourceUI(Salt_amount, Salt_share, Salt_count, Total_resources);
            SetResourceUI(Gold_amount, Gold_share, Gold_count, Total_resources);
            SetResourceUI(Gemstones_amount, Gemstones_share, Gemstones_count, Total_resources);
            SetResourceUI(Stone_amount, Stone_share, Stone_count, Total_resources);

            //Write the edited file back to disk
            File.WriteAllBytes(WorkingFileName, DnG_map);

            Resources_wait.Visible = false;
            Tab_control.Enabled = true;
        }

        private void SetResourceUI(Control amountControl, Control shareControl, int count, int total)
        {
            amountControl.Text = count.ToString();
            shareControl.Text = total == 0
                ? "NaN%"
                : (count * 100.0 / total).ToString("F2") + "%";
        }

        // Helper to find the index where a byte sequence starts
        private int FindSequenceOffset(byte[] data, byte[] pattern, int startIndex = 0)
        {
            for (int i = startIndex; i <= data.Length - pattern.Length; i++)
            {
                if (MatchBytes(data, i, pattern))
                    return i + pattern.Length; // Returns offset right after header
            }
            return -1; // Not found
        }

        // Helper method for fast byte array comparison
        bool MatchBytes(byte[] source, int offset, byte[] target)
        {
            if (offset + target.Length > source.Length) return false;

            for (int i = 0; i < target.Length; i++)
            {
                if (source[offset + i] != target[i])
                    return false;
            }
            return true;
        }

        //Checking if the resource is not under water
        bool IsOnLand(byte[] DnG_map, int Heights_array_beginning, int Index_logical, int Map_size_x)
        {
            //Convert to detailed grid coordinates
            int x_logical = Index_logical % Map_size_x;
            int y_logical = Index_logical / Map_size_x;

            int x_detailed;
            if (y_logical % 2 == 1)
            {
                x_detailed = x_logical * 4 + 2;
            }
            else
            {
                x_detailed = x_logical * 4;
            }

            int y_detailed = y_logical * 4;

            int Index_detailed = x_detailed + (y_detailed * (Map_size_x * 4));

            return BitConverter.ToInt32(DnG_map, Heights_array_beginning + Index_detailed * 4) > -100;
        }

        public static bool IsRockTexture(byte[] DnG_map, int Index, int Textures_array_beginning)
        {
            // Convert 4 bytes to uint
            uint value = BitConverter.ToUInt32(DnG_map, Textures_array_beginning + Index * 4);

            // Convert to Big-Endian to match human-readable hex values
            if (BitConverter.IsLittleEndian)
            {
                value = (value >> 24) |
                       ((value >> 8) & 0x0000FF00) |
                       ((value << 8) & 0x00FF0000) |
                       (value << 24);
            }

            switch (value)
            {
                case 0xFEAF0FD0: // 0: rock
                case 0xEFBEADDE: // 1: rock big
                case 0xFECAFECA: // 2: rock small
                case 0xFFCAFECA: // 3: rocky earth
                case 0x00CBFECA: // 4: rock stretched x
                case 0x01CBFECA: // 5: rock stretched y
                case 0x02CBFECA: // 6: rocky earth big
                case 0x03CBFECA: // 7: rocky plants
                case 0x04CBFECA: // 8: rocky earth dark
                case 0x04DECADE: // 9: LAVA rock
                case 0x05DECADE: // 10: LAVA rock big
                case 0x06DECADE: // 11: LAVA rock small
                case 0xB0FA87CA: // 12: LAVA rock floating lava
                case 0x80A51CFA: // 13: MED rock
                case 0x81A51CFA: // 14: MED rock big
                case 0x82A51CFA: // 15: MED rock small
                case 0x83A51CFA: // 16: MED rock red
                case 0x84A51CFA: // 17: MED rock red small
                case 0x85A51CFA: // 18: MED rock red big
                case 0x86A51CFA: // 19: MED rocky earth big
                case 0x87A51CFA: // 20: MED rocky plants
                case 0x88A51CFA: // 21: MED rocky earth dark
                case 0x89A51CFA: // 22: MED rocky earth
                    return true;

                default:
                    return false;
            }
        }

        //Map renaming dialog
        private void Map_name_button_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            using (var Map_rename_dialog = new Map_renaming(Map_name_button.Text))
            {
                if (Map_rename_dialog.ShowDialog(this) == DialogResult.OK)
                {
                    Map_name_button.Text = Map_rename_dialog.Map_name;
                    UpdateMapName();
                }
            }
        }

        // Update the map name in the DnG map file
        void UpdateMapName()
        {
            byte[] DnG_map = File.ReadAllBytes(WorkingFileName);
            int current_byte = 0;
            //Skip the header
            current_byte += 12;
            //Skip start positions and the player count
            current_byte += 20 * (int)BitConverter.ToUInt32(DnG_map, current_byte) + 4;
            //Read the current map name length
            int Map_name_length = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            //Replace the map name with the new one
            byte[] nameBytes = System.Text.Encoding.UTF8.GetBytes(Map_name_button.Text);
            byte[] lengthBytes = BitConverter.GetBytes(nameBytes.Length);
            byte[] combinedData = lengthBytes.Concat(nameBytes).ToArray();

            DnG_map = ReplaceSection(DnG_map, current_byte, Map_name_length + 4, combinedData);

            //Write the edited file back to disk
            File.WriteAllBytes(WorkingFileName, DnG_map);
        }

        public static byte[] ReplaceSection(byte[] original, int startIndex, int lengthToRemove, byte[] insertData)
        {
            // 1. Validate inputs to prevent out-of-bounds exceptions
            if (original == null) throw new ArgumentNullException(nameof(original));
            if (insertData == null) throw new ArgumentNullException(nameof(insertData));
            if (startIndex < 0 || startIndex > original.Length) throw new ArgumentOutOfRangeException(nameof(startIndex));
            if (lengthToRemove < 0 || startIndex + lengthToRemove > original.Length) throw new ArgumentOutOfRangeException(nameof(lengthToRemove));

            // 2. Calculate the size of the new array
            int newSize = original.Length - lengthToRemove + insertData.Length;
            byte[] result = new byte[newSize];

            // 3. Copy the beginning segment (before the replaced section)
            if (startIndex > 0)
            {
                Buffer.BlockCopy(original, 0, result, 0, startIndex);
            }

            // 4. Copy the new inserted data
            if (insertData.Length > 0)
            {
                Buffer.BlockCopy(insertData, 0, result, startIndex, insertData.Length);
            }

            // 5. Copy the trailing segment (after the replaced section)
            int tailLength = original.Length - (startIndex + lengthToRemove);
            if (tailLength > 0)
            {
                Buffer.BlockCopy(
                    original,
                    startIndex + lengthToRemove,          // Source offset: skip the removed part
                    result,
                    startIndex + insertData.Length,       // Destination offset: right after the inserted data
                    tailLength);
            }

            return result;
        }

        //Resource share recommendations
        private void Share_button_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            MessageBox.Show("Recommended resource shares:\n\nCoal: ~40%\nIron: ~20%\nSalt: ~20%\nGold: ~20%\nGemstones: ~3%\nStone: ~3%", "Recommended resource shares", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Resources_swap_button_Click(object sender, EventArgs e)
        {
            //Check if the user has selected both resources to swap
            if (Resources_from_list.SelectedIndex == -1 || Resources_to_list.SelectedIndex == -1)
            {
                MessageBox.Show("Please select both resources to swap.", "No resources selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            Resources_wait.Visible = true;
            Tab_control.Enabled = false;
            Resources_wait.Refresh();

            byte[] Resources_list = { 0xD3, 0xDC, 0x68, 0x70, 0xBE, 0x20, 0x50, 0xEC, 0x23, 0xD6, 0xD2, 0x09, 0x33, 0xC6, 0x41, 0x4F, 0x03, 0xC9, 0x98, 0xCB, 0xD3, 0x52, 0xE9, 0x55 };

            uint From_resource = (uint)BitConverter.ToUInt32(Resources_list, Resources_from_list.SelectedIndex * 4);
            uint To_resource = (uint)BitConverter.ToUInt32(Resources_list, Resources_to_list.SelectedIndex * 4);
            byte[] To_resource_bytes = BitConverter.GetBytes(To_resource);

            byte[] DnG_map = File.ReadAllBytes(WorkingFileName);
            int current_byte = 0;

            //Finding the resource array header in the map file
            current_byte = FindSequenceOffset(DnG_map, Resources_header, current_byte);

            if (current_byte == -1)
            {
                MessageBox.Show("Resource array not found in the map file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Tab_control.Enabled = true;
                Resources_wait.Visible = false;
                return;
            }

            // Reading map size and calculating resource array length
            int Map_size_x = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            current_byte += 4;
            int Map_size_y = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            current_byte += 4;
            int Resource_array_length = Map_size_x * Map_size_y;
            //Skip first resource amount
            current_byte += 4;

            for (int i = 0; i < Resource_array_length; i++)
            {
                if (From_resource == (uint)BitConverter.ToUInt32(DnG_map, current_byte))
                {
                    Array.Copy(To_resource_bytes, 0, DnG_map, current_byte, 4);
                }
                current_byte += 8;
            }

            //Write the edited file back to disk
            File.WriteAllBytes(WorkingFileName, DnG_map);

            //Recalculating the resource shares after the resources have been swapped
            UpdateResources(0, DnG_map);
        }

        //Save the map for further editing
        private void Continue_editing_button_Click(object sender, EventArgs e)
        {
            // Allow the user to keep the old map name even if it exceeds the maximum length, but warn them about it.
            if (Map_name_button.Text.Length > 20)
            {
                MessageBox.Show("Current map name with a length of " + Map_name_button.Text.Length + " characters is larger than the maximum allowed of 20 characters", "Map name is too long", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            using (SaveFileDialog saveFileDialog = new SaveFileDialog())
            {
                saveFileDialog.Filter = "DnG map file (*.s2m)|*.s2m|All files (*.*)|*.*";
                saveFileDialog.Title = "Save the map for further editing";
                saveFileDialog.InitialDirectory = Path.GetDirectoryName(DnG_map_path.Text);
                saveFileDialog.FileName = Path.GetFileName(DnG_map_path.Text);
                if (saveFileDialog.ShowDialog() == DialogResult.OK)
                {
                    //Allow the user to keep the new file name even if it exceeds the maximum length, but warn them about it.
                    if (Path.GetFileNameWithoutExtension(saveFileDialog.FileName).Length > 20)
                    {
                        MessageBox.Show("Current file name with a length of " + Path.GetFileNameWithoutExtension(saveFileDialog.FileName).Length + " characters is larger than the maximum allowed of 20 characters", "File name is too long", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }

                    DnG = true;
                    Compress = true;
                    sourceFileName = WorkingFileName;
                    destinationFileName = saveFileDialog.FileName;

                    Archiver();
                }
            }
        }

        // Helper method to handle all swap additions
        private void AddSwapEntry(ListBox fromList, ListBox toList, int typeId, string warningMessage)
        {
            if (fromList.SelectedIndex == -1 || toList.SelectedIndex == -1)
            {
                MessageBox.Show(warningMessage, "Selection Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            string displayText = $"{fromList.Text} -> {toList.Text}";

            if (!Swap_list_view.Items.Contains(displayText))
            {
                Swap_list.Add((typeId, fromList.SelectedIndex, toList.SelectedIndex));
                Swap_list_view.Items.Add(displayText);
            }
        }

        // Cleaned-up event handlers:
        private void Textures_swap_button_Click(object sender, EventArgs e) =>
            AddSwapEntry(Textures_from_list, Textures_to_list, 1, "Please select both textures to swap.");

        private void Logical_grid_swap_button_Click(object sender, EventArgs e) =>
            AddSwapEntry(Logical_grid_from_list, Logical_grid_to_list, 2, "Please select both objects to swap.");

        private void Small_doodads_swap_button_Click(object sender, EventArgs e) =>
            AddSwapEntry(Small_doodads_from_list, Small_doodads_to_list, 3, "Please select both doodads to swap.");

        private void Swap_move_down_button_Click(object sender, EventArgs e)
        {
            int index = Swap_list_view.SelectedIndex;

            // Ensure an item is selected and it isn't already the last item
            if (index != -1 && index < Swap_list_view.Items.Count - 1)
            {
                // 1. Swap elements in the backend data list
                var temp = Swap_list[index];
                Swap_list[index] = Swap_list[index + 1];
                Swap_list[index + 1] = temp;

                // 2. Swap elements in the ListBox UI
                object selectedItem = Swap_list_view.SelectedItem;
                Swap_list_view.Items.RemoveAt(index);
                Swap_list_view.Items.Insert(index + 1, selectedItem);

                // 3. Keep focus on the moved item
                Swap_list_view.SelectedIndex = index + 1;
            }
        }

        private void Swap_remove_button_Click(object sender, EventArgs e)
        {
            int index = Swap_list_view.SelectedIndex;

            // Ensure an item is actually selected
            if (index != -1)
            {
                // 1. Remove element from the backend data list
                Swap_list.RemoveAt(index);

                // 2. Remove element from the ListBox UI
                Swap_list_view.Items.RemoveAt(index);

                // 3. Maintain active selection if items remain
                if (Swap_list_view.Items.Count > 0)
                {
                    // Select the item at the same position, or the last item if the end item was removed
                    Swap_list_view.SelectedIndex = Math.Min(index, Swap_list_view.Items.Count - 1);
                }
            }
        }

        private void Swap_move_up_button_Click(object sender, EventArgs e)
        {
            int index = Swap_list_view.SelectedIndex;

            // Ensure an item is selected and it isn't already the first item (index 0)
            if (index > 0)
            {
                // 1. Swap elements in the backend data list
                var temp = Swap_list[index];
                Swap_list[index] = Swap_list[index - 1];
                Swap_list[index - 1] = temp;

                // 2. Swap elements in the ListBox UI
                object selectedItem = Swap_list_view.SelectedItem;
                Swap_list_view.Items.RemoveAt(index);
                Swap_list_view.Items.Insert(index - 1, selectedItem);

                // 3. Keep focus on the moved item
                Swap_list_view.SelectedIndex = index - 1;
            }
        }

        private void Harbours_add_button_Click(object sender, EventArgs e)
        {
            isUpdatingUI = true;

            //Default
            Harbours_list.Add((0, 0, -1, false, 0, 0, 0, 0));

            UpdateBuoyDropdownItems();

            //Add the item to the visual list and select it
            Harbours_list_view.Items.Add((Harbours_list_view.Items.Count + 1).ToString());
            Harbours_list_view.SelectedIndex = Harbours_list_view.Items.Count - 1;

            UpdateHarbourPanel();

            isUpdatingUI = false;
        }

        // Rebuilds buoy options whenever harbour count changes
        private void UpdateBuoyDropdownItems()
        {
            bool previousUpdatingState = isUpdatingUI;
            isUpdatingUI = true;

            Buoy_1_connection_select.Items.Clear();
            Buoy_2_connection_select.Items.Clear();

            Buoy_1_connection_select.Items.Add("None");
            Buoy_2_connection_select.Items.Add("None");

            for (int i = 0; i < Harbours_list.Count; i++)
            {
                Buoy_1_connection_select.Items.Add($"Harbour {i + 1} buoy 1");
                Buoy_1_connection_select.Items.Add($"Harbour {i + 1} buoy 2");
                Buoy_2_connection_select.Items.Add($"Harbour {i + 1} buoy 1");
                Buoy_2_connection_select.Items.Add($"Harbour {i + 1} buoy 2");
            }

            // Restore previous state instead of forcing false
            isUpdatingUI = previousUpdatingState;
        }

        void UpdateHarbourPanel()
        {
            isUpdatingUI = true; // Disable saving to list while we populate the controls
            int index = Harbours_list_view.SelectedIndex;

            if (index >= 0 && index < Harbours_list.Count)
            {
                Harbour_panel.Enabled = true;

                // Load current selection from the list
                var selectedHarbour = Harbours_list[Harbours_list_view.SelectedIndex];

                Harbour_position_X_input.Value = selectedHarbour.pos_x;
                Harbour_position_Y_input.Value = selectedHarbour.pos_y;
                Harbour_rotation_select.SelectedIndex = selectedHarbour.rotation;

                Harbour_anchor_checkbox.Checked = selectedHarbour.anchorage;
                Harbour_anchor_panel.Enabled = selectedHarbour.anchorage;

                Anchor_position_X_input.Value = selectedHarbour.anchor_x;
                Anchor_position_Y_input.Value = selectedHarbour.anchor_y;
                Buoy_1_connection_select.SelectedIndex = selectedHarbour.buoy_1_connection;
                Buoy_2_connection_select.SelectedIndex = selectedHarbour.buoy_2_connection;
            }
            else
            {
                Harbour_panel.Enabled = false;

                //Load default preset
                Harbour_position_X_input.Value = 0;
                Harbour_position_Y_input.Value = 0;
                Harbour_rotation_select.SelectedIndex = -1;

                Harbour_anchor_checkbox.Checked = false;
                Harbour_anchor_panel.Enabled = false;

                Anchor_position_X_input.Value = 0;
                Anchor_position_Y_input.Value = 0;
                //"None"
                Buoy_1_connection_select.SelectedIndex = 0;
                Buoy_2_connection_select.SelectedIndex = 0;
            }

            isUpdatingUI = false; // Re-enable saving to list
        }

        private void Harbours_list_view_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateHarbourPanel();
        }

        // Remove the currently selected index
        private void Harbours_remove_button_Click(object sender, EventArgs e)
        {
            isUpdatingUI = true;

            int index = Harbours_list_view.SelectedIndex;

            if (index >= 0 && index < Harbours_list.Count)
            {
                int removedBuoy1Index = index * 2 + 1;
                int removedBuoy2Index = index * 2 + 2;

                Harbours_list.RemoveAt(index);
                Harbours_list_view.Items.RemoveAt(index);

                for (int i = 0; i < Harbours_list_view.Items.Count; i++)
                {
                    Harbours_list_view.Items[i] = (i + 1).ToString();
                }

                for (int i = 0; i < Harbours_list.Count; i++)
                {
                    var (px, py, rot, anch, ax, ay, b1, b2) = Harbours_list[i];

                    if (b1 == removedBuoy1Index || b1 == removedBuoy2Index)
                        b1 = 0;
                    else if (b1 > removedBuoy2Index)
                        b1 -= 2;

                    if (b2 == removedBuoy1Index || b2 == removedBuoy2Index)
                        b2 = 0;
                    else if (b2 > removedBuoy2Index)
                        b2 -= 2;

                    Harbours_list[i] = (px, py, rot, anch, ax, ay, b1, b2);
                }

                UpdateBuoyDropdownItems();

                int newIndex = Math.Min(index, Harbours_list_view.Items.Count - 1);

                isUpdatingUI = false; // Re-enable UI events before setting index

                // If the index didn't change (e.g. removed the last item), fire manually.
                // Otherwise, setting SelectedIndex will trigger SelectedIndexChanged -> UpdateHarbourPanel.
                if (Harbours_list_view.SelectedIndex == newIndex)
                {
                    UpdateHarbourPanel();
                }
                else
                {
                    Harbours_list_view.SelectedIndex = newIndex;
                }
            }
            else
            {
                isUpdatingUI = false;
            }
        }

        // This method is called by ALL input change events (8)
        private void SaveCurrentHarbourData(object sender, EventArgs e)
        {
            if (isUpdatingUI || Harbours_list_view.SelectedIndex < 0) return;

            int index = Harbours_list_view.SelectedIndex;

            int ownBuoy1Index = index * 2 + 1;
            int ownBuoy2Index = index * 2 + 2;

            int oldSelectedB1 = Harbours_list[index].buoy_1_connection;
            int oldSelectedB2 = Harbours_list[index].buoy_2_connection;

            int selectedB1 = Buoy_1_connection_select.SelectedIndex;
            int selectedB2 = Buoy_2_connection_select.SelectedIndex;
            bool selectionCorrected = false;

            // 1. Prevent self-connection
            if (selectedB1 == ownBuoy1Index || selectedB1 == ownBuoy2Index)
            {
                selectedB1 = 0;
                selectionCorrected = true;
            }

            // 2. Prevent self-connection or duplicate target assignment
            if (selectedB2 == ownBuoy1Index || selectedB2 == ownBuoy2Index || (selectedB2 != 0 && selectedB2 == selectedB1))
            {
                selectedB2 = 0;
                selectionCorrected = true;
            }

            // 3. Handle Reciprocal Links for Buoy 1
            if (selectedB1 != oldSelectedB1)
            {
                // Only break old target if Buoy 2 isn't now pointing to it
                if (oldSelectedB1 != 0 && oldSelectedB1 != selectedB2)
                    SetBuoyConnection(oldSelectedB1, 0);

                if (selectedB1 != 0)
                {
                    int previousTargetOfNewB1 = GetBuoyConnection(selectedB1);
                    if (previousTargetOfNewB1 != 0)
                    {
                        SetBuoyConnection(previousTargetOfNewB1, 0);
                    }
                    SetBuoyConnection(selectedB1, ownBuoy1Index);
                }
            }

            // 4. Handle Reciprocal Links for Buoy 2
            if (selectedB2 != oldSelectedB2)
            {
                // Only break old target if Buoy 1 isn't now pointing to it
                if (oldSelectedB2 != 0 && oldSelectedB2 != selectedB1)
                    SetBuoyConnection(oldSelectedB2, 0);

                if (selectedB2 != 0)
                {
                    int previousTargetOfNewB2 = GetBuoyConnection(selectedB2);
                    if (previousTargetOfNewB2 != 0)
                    {
                        SetBuoyConnection(previousTargetOfNewB2, 0);
                    }
                    SetBuoyConnection(selectedB2, ownBuoy2Index);
                }
            }

            // 5. Update UI safely if corrected
            if (selectionCorrected)
            {
                isUpdatingUI = true;
                Buoy_1_connection_select.SelectedIndex = selectedB1;
                Buoy_2_connection_select.SelectedIndex = selectedB2;
                isUpdatingUI = false;
            }

            // 6. Save data
            Harbours_list[index] = (
                (int)Harbour_position_X_input.Value,
                (int)Harbour_position_Y_input.Value,
                Harbour_rotation_select.SelectedIndex,
                Harbour_anchor_checkbox.Checked,
                (int)Anchor_position_X_input.Value,
                (int)Anchor_position_Y_input.Value,
                selectedB1,
                selectedB2
            );

            Harbour_anchor_panel.Enabled = Harbour_anchor_checkbox.Checked;
        }

        // Helper to find out what a specific buoy is currently pointing to
        private int GetBuoyConnection(int buoyDropdownIndex)
        {
            if (buoyDropdownIndex == 0) return 0;

            int harbourIndex = (buoyDropdownIndex - 1) / 2;
            bool isBuoy1 = buoyDropdownIndex % 2 != 0;

            var h = Harbours_list[harbourIndex];
            return isBuoy1 ? h.buoy_1_connection : h.buoy_2_connection;
        }

        // Helper to overwrite a specific buoy's connection in the background
        private void SetBuoyConnection(int buoyDropdownIndex, int newTargetIndex)
        {
            if (buoyDropdownIndex == 0) return;

            int harbourIndex = (buoyDropdownIndex - 1) / 2;
            bool isBuoy1 = buoyDropdownIndex % 2 != 0;

            var h = Harbours_list[harbourIndex];

            // Rebuild and replace the tuple for that specific harbour
            if (isBuoy1)
                Harbours_list[harbourIndex] = (h.pos_x, h.pos_y, h.rotation, h.anchorage, h.anchor_x, h.anchor_y, newTargetIndex, h.buoy_2_connection);
            else
                Harbours_list[harbourIndex] = (h.pos_x, h.pos_y, h.rotation, h.anchorage, h.anchor_x, h.anchor_y, h.buoy_1_connection, newTargetIndex);
        }

        private void Caves_add_button_Click(object sender, EventArgs e)
        {
            isUpdatingUI = true;

            // Default value: (X, Y, Type)
            Caves_list.Add((0, 0, -1));

            // Add the item to the visual list and select it
            Caves_list_view.Items.Add((Caves_list_view.Items.Count + 1).ToString());
            Caves_list_view.SelectedIndex = Caves_list_view.Items.Count - 1;

            UpdateCavePanel();

            isUpdatingUI = false;
        }

        private void Caves_list_view_SelectedIndexChanged(object sender, EventArgs e)
        {
            UpdateCavePanel();
        }

        // Remove the currently selected index
        private void Caves_remove_button_Click(object sender, EventArgs e)
        {
            isUpdatingUI = true;

            int index = Caves_list_view.SelectedIndex;

            if (index >= 0 && index < Caves_list.Count)
            {
                // Remove the item from both data source and UI control
                Caves_list.RemoveAt(index);
                Caves_list_view.Items.RemoveAt(index);

                // Re-label remaining items to keep numbering continuous (1, 2, 3...)
                for (int i = 0; i < Caves_list_view.Items.Count; i++)
                {
                    Caves_list_view.Items[i] = (i + 1).ToString();
                }

                isUpdatingUI = false; // Re-enable UI events before changing the selection

                // Determine new index:
                int newIndex = Math.Min(index, Caves_list_view.Items.Count - 1);

                // If the index didn't change (e.g., removed the last item), force a panel update.
                // Otherwise, setting the index will automatically trigger SelectedIndexChanged.
                if (Caves_list_view.SelectedIndex == newIndex)
                {
                    UpdateCavePanel();
                }
                else
                {
                    Caves_list_view.SelectedIndex = newIndex;
                }
            }
            else
            {
                isUpdatingUI = false;
            }
        }

        private void UpdateCavePanel()
        {
            isUpdatingUI = true; // Disable saving to list while we populate the controls

            int index = Caves_list_view.SelectedIndex;

            if (index >= 0 && index < Caves_list.Count)
            {
                Cave_panel.Enabled = true;

                // Load current selection from the list by destructuring the tuple
                var (posX, posY, type) = Caves_list[index];

                Cave_position_X_input.Value = posX;
                Cave_position_Y_input.Value = posY;
                Cave_type_select.SelectedIndex = type;
            }
            else
            {
                Cave_panel.Enabled = false;

                // Load default preset
                Cave_position_X_input.Value = 0;
                Cave_position_Y_input.Value = 0;
                Cave_type_select.SelectedIndex = -1;
            }

            isUpdatingUI = false; // Re-enable saving to list
        }

        // This method is called by ALL input change events (3)
        private void SaveCurrentCaveData(object sender, EventArgs e)
        {
            // Don't save if we are just loading the UI or if nothing is selected
            if (isUpdatingUI || Caves_list_view.SelectedIndex < 0) return;

            int index = Caves_list_view.SelectedIndex;

            Caves_list[index] = (
                (int)Cave_position_X_input.Value,
                (int)Cave_position_Y_input.Value,
                Cave_type_select.SelectedIndex
            );
        }

        // Sacrifices amount update
        private void UpdateUsageStatus(System.Windows.Forms.ListView listView, Label label, string factionName, int maxLimit)
        {
            int selectedCount = listView.CheckedItems.Count;
            label.Text = $"{factionName} {selectedCount}/{maxLimit}";

            if (selectedCount < maxLimit)
            {
                label.ForeColor = Color.DarkGreen;
            }
            else if (selectedCount == maxLimit)
            {
                label.ForeColor = Color.DarkGoldenrod;
            }
            else
            {
                label.ForeColor = Color.DarkRed;
            }
        }

        // --- Event Handlers (No Research) ---

        private void Sacrifices_no_research_Bavarians_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_no_research_Bavarians, No_research_Bavarians_usage, "Bavarians", 4);

        private void Sacrifices_no_research_Egyptians_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_no_research_Egyptians, No_research_Egyptians_usage, "Egyptians", 4);

        private void Sacrifices_no_research_Scots_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_no_research_Scots, No_research_Scots_usage, "Scots", 4);

        // --- Event Handlers (Research) ---

        private void Sacrifices_research_Bavarians_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_research_Bavarians, Research_Bavarians_usage, "Bavarians", 8);

        private void Sacrifices_research_Egyptians_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_research_Egyptians, Research_Egyptians_usage, "Egyptians", 8);

        private void Sacrifices_research_Scots_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_research_Scots, Research_Scots_usage, "Scots", 8);

        // --- Preset Helpers ---

        private string GetCheckedIndices(System.Windows.Forms.ListView lv)
        {
            return string.Join(",", lv.CheckedIndices.Cast<int>());
        }

        private void SetCheckedIndices(System.Windows.Forms.ListView lv, string indicesStr)
        {
            foreach (ListViewItem item in lv.Items)
            {
                item.Checked = false;
            }

            if (string.IsNullOrWhiteSpace(indicesStr)) return;

            foreach (string idxStr in indicesStr.Split(','))
            {
                if (int.TryParse(idxStr.Trim(), out int idx) && idx >= 0 && idx < lv.Items.Count)
                {
                    lv.Items[idx].Checked = true;
                }
            }
        }

        private string GetSwapDisplayText(int tab, int from, int to)
        {
            try
            {
                switch (tab)
                {
                    case 1:
                        if (from < Textures_from_list.Items.Count && to < Textures_to_list.Items.Count)
                            return $"{Textures_from_list.Items[from]} -> {Textures_to_list.Items[to]}";
                        break;
                    case 2:
                        if (from < Logical_grid_from_list.Items.Count && to < Logical_grid_to_list.Items.Count)
                            return $"{Logical_grid_from_list.Items[from]} -> {Logical_grid_to_list.Items[to]}";
                        break;
                    case 3:
                        if (from < Small_doodads_from_list.Items.Count && to < Small_doodads_to_list.Items.Count)
                            return $"{Small_doodads_from_list.Items[from]} -> {Small_doodads_to_list.Items[to]}";
                        break;
                }
            }
            catch { }

            return $"Swap (Tab {tab}): {from} -> {to}";
        }

        // --- Sacrifice Presets ---

        private void Sacrifice_preset_export_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog sfd = new SaveFileDialog { Filter = "DnG-AdK-Mapedit sacrifice preset (*.dams)|*.dams", Title = "Export Sacrifice Preset" })
            {
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    var lines = new List<string>
                    {
                        GetCheckedIndices(Sacrifices_no_research_Bavarians),
                        GetCheckedIndices(Sacrifices_no_research_Egyptians),
                        GetCheckedIndices(Sacrifices_no_research_Scots),
                        GetCheckedIndices(Sacrifices_research_Bavarians),
                        GetCheckedIndices(Sacrifices_research_Egyptians),
                        GetCheckedIndices(Sacrifices_research_Scots)
                    };

                    File.WriteAllLines(sfd.FileName, lines);
                }
            }
        }
        private void Sacrifice_preset_load_Click(object sender, EventArgs e)
        {
            try
            {
                string[] lines = null;

                // 1. Check if we should load from embedded resources
                if (Sacrifice_included_checkbox.Checked)
                {
                    string selectedPreset = Sacrifice_included_presets.SelectedItem?.ToString();

                    if (string.IsNullOrEmpty(selectedPreset))
                    {
                        MessageBox.Show("Please select an embedded preset from the list.", "No Preset Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }

                    var assembly = Assembly.GetExecutingAssembly();

                    // Locate the resource ending with "{selectedPreset}.dams" (case-insensitive)
                    string resourceName = assembly.GetManifestResourceNames()
                        .FirstOrDefault(r => r.EndsWith($"{selectedPreset}.dams", StringComparison.OrdinalIgnoreCase));

                    if (resourceName == null)
                    {
                        MessageBox.Show($"Embedded preset '{selectedPreset}' was not found in assembly resources.", "Error Loading Preset", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return;
                    }

                    // Read lines directly from the embedded stream
                    using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        List<string> lineList = new List<string>();
                        string line;
                        while ((line = reader.ReadLine()) != null)
                        {
                            lineList.Add(line);
                        }
                        lines = lineList.ToArray();
                    }
                }
                else
                {
                    // 2. Load from file on disk via dialog
                    using (OpenFileDialog ofd = new OpenFileDialog { Filter = "DnG-AdK-Mapedit sacrifice preset (*.dams)|*.dams", Title = "Load Sacrifice Preset" })
                    {
                        if (ofd.ShowDialog() == DialogResult.OK)
                        {
                            lines = File.ReadAllLines(ofd.FileName);
                        }
                        else
                        {
                            return; // User cancelled
                        }
                    }
                }

                // 3. Apply the preset data to controls
                if (lines != null && lines.Length >= 6)
                {
                    SetCheckedIndices(Sacrifices_no_research_Bavarians, lines[0]);
                    SetCheckedIndices(Sacrifices_no_research_Egyptians, lines[1]);
                    SetCheckedIndices(Sacrifices_no_research_Scots, lines[2]);
                    SetCheckedIndices(Sacrifices_research_Bavarians, lines[3]);
                    SetCheckedIndices(Sacrifices_research_Egyptians, lines[4]);
                    SetCheckedIndices(Sacrifices_research_Scots, lines[5]);
                }
                else
                {
                    MessageBox.Show("The preset file format is invalid or incomplete.", "Error Loading Preset", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show("Failed to load sacrifice preset:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void Sacrifice_included_checkbox_CheckedChanged(object sender, EventArgs e)
        {
            if (Sacrifice_included_checkbox.Checked)
            {
                Sacrifice_included_presets.Enabled = true;
            }
            else
            {
                Sacrifice_included_presets.Enabled = false;
            }
        }

        // --- Map Presets ---

        private void Map_preset_export_Click(object sender, EventArgs e)
        {
            using (SaveFileDialog sfd = new SaveFileDialog { Filter = "DnG-AdK-Mapedit map preset (*.damp)|*.damp", Title = "Export Map Preset" })
            {
                if (sfd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        using (StreamWriter sw = new StreamWriter(sfd.FileName))
                        {
                            sw.WriteLine("[MAP_NAME]");
                            sw.WriteLine(Map_name_button.Text);

                            sw.WriteLine("[SWAPS]");
                            foreach (var swap in Swap_list)
                                sw.WriteLine($"{swap.tab},{swap.from},{swap.to}");

                            sw.WriteLine("[HARBOURS]");
                            foreach (var h in Harbours_list)
                                sw.WriteLine($"{h.pos_x},{h.pos_y},{h.rotation},{h.anchorage},{h.anchor_x},{h.anchor_y},{h.buoy_1_connection},{h.buoy_2_connection}");

                            sw.WriteLine("[CAVES]");
                            foreach (var c in Caves_list)
                                sw.WriteLine($"{c.pos_x},{c.pos_y},{c.type}");

                            sw.WriteLine("[SACRIFICES]");
                            sw.WriteLine(GetCheckedIndices(Sacrifices_no_research_Bavarians));
                            sw.WriteLine(GetCheckedIndices(Sacrifices_no_research_Egyptians));
                            sw.WriteLine(GetCheckedIndices(Sacrifices_no_research_Scots));
                            sw.WriteLine(GetCheckedIndices(Sacrifices_research_Bavarians));
                            sw.WriteLine(GetCheckedIndices(Sacrifices_research_Egyptians));
                            sw.WriteLine(GetCheckedIndices(Sacrifices_research_Scots));

                            sw.WriteLine("[COLOURS]");
                            sw.WriteLine(Player_count);
                            var selectors = new[]
                            {
                                Player_1_select, Player_2_select, Player_3_select,
                                Player_4_select, Player_5_select, Player_6_select
                            };
                            sw.WriteLine(string.Join(",", selectors.Take(Player_count).Select(s => s.SelectedIndex)));
                        }
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show("Failed to export map preset:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        private void Map_preset_load_Click(object sender, EventArgs e)
        {
            using (OpenFileDialog ofd = new OpenFileDialog { Filter = "DnG-AdK-Mapedit map preset (*.damp)|*.damp", Title = "Load Map Preset" })
            {
                if (ofd.ShowDialog() == DialogResult.OK)
                {
                    try
                    {
                        isUpdatingUI = true;

                        string[] lines = File.ReadAllLines(ofd.FileName);
                        string currentSection = "";
                        int sacrificeLine = 0;
                        int colourLine = 0;
                        int savedPlayerCount = 0;

                        Swap_list.Clear();
                        Swap_list_view.Items.Clear();
                        Harbours_list.Clear();
                        Harbours_list_view.Items.Clear();
                        Caves_list.Clear();
                        Caves_list_view.Items.Clear();

                        foreach (string rawLine in lines)
                        {
                            string line = rawLine.Trim();

                            // Detect section header
                            if (line.StartsWith("[") && line.EndsWith("]"))
                            {
                                currentSection = line;
                                if (currentSection == "[SACRIFICES]") sacrificeLine = 0;
                                if (currentSection == "[COLOURS]") colourLine = 0;
                                continue;
                            }

                            if (currentSection == "[MAP_NAME]")
                            {
                                if (!string.IsNullOrEmpty(line))
                                {
                                    Map_name_button.Text = line;
                                    UpdateMapName();
                                }
                            }
                            else if (currentSection == "[SWAPS]")
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                var parts = line.Split(',');
                                if (parts.Length == 3 && int.TryParse(parts[0], out int tab) && int.TryParse(parts[1], out int from) && int.TryParse(parts[2], out int to))
                                {
                                    Swap_list.Add((tab, from, to));
                                    Swap_list_view.Items.Add(GetSwapDisplayText(tab, from, to));
                                }
                            }
                            else if (currentSection == "[HARBOURS]")
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                var parts = line.Split(',');
                                if (parts.Length == 8 &&
                                    int.TryParse(parts[0], out int px) && int.TryParse(parts[1], out int py) &&
                                    int.TryParse(parts[2], out int rot) && bool.TryParse(parts[3], out bool anch) &&
                                    int.TryParse(parts[4], out int ax) && int.TryParse(parts[5], out int ay) &&
                                    int.TryParse(parts[6], out int b1) && int.TryParse(parts[7], out int b2))
                                {
                                    Harbours_list.Add((px, py, rot, anch, ax, ay, b1, b2));
                                    Harbours_list_view.Items.Add(Harbours_list.Count.ToString());
                                }
                            }
                            else if (currentSection == "[CAVES]")
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;
                                var parts = line.Split(',');
                                if (parts.Length == 3 &&
                                    int.TryParse(parts[0], out int cx) && int.TryParse(parts[1], out int cy) &&
                                    int.TryParse(parts[2], out int ct))
                                {
                                    Caves_list.Add((cx, cy, ct));
                                    Caves_list_view.Items.Add(Caves_list.Count.ToString());
                                }
                            }
                            else if (currentSection == "[SACRIFICES]")
                            {
                                // Do NOT skip empty lines here; empty string means 0 items selected for this faction
                                switch (sacrificeLine)
                                {
                                    case 0: SetCheckedIndices(Sacrifices_no_research_Bavarians, line); break;
                                    case 1: SetCheckedIndices(Sacrifices_no_research_Egyptians, line); break;
                                    case 2: SetCheckedIndices(Sacrifices_no_research_Scots, line); break;
                                    case 3: SetCheckedIndices(Sacrifices_research_Bavarians, line); break;
                                    case 4: SetCheckedIndices(Sacrifices_research_Egyptians, line); break;
                                    case 5: SetCheckedIndices(Sacrifices_research_Scots, line); break;
                                }
                                sacrificeLine++;
                            }
                            else if (currentSection == "[COLOURS]")
                            {
                                if (string.IsNullOrWhiteSpace(line)) continue;

                                if (colourLine == 0)
                                {
                                    int.TryParse(line, out savedPlayerCount);
                                    colourLine++;
                                }
                                else if (colourLine == 1)
                                {
                                    // Only load color selections if saved player count matches the current map's player count
                                    if (savedPlayerCount == Player_count)
                                    {
                                        var selectors = new[]
                                        {
                                            Player_1_select, Player_2_select, Player_3_select,
                                            Player_4_select, Player_5_select, Player_6_select
                                        };

                                        var parts = line.Split(',');
                                        int maxPlayers = Math.Min(parts.Length, Math.Min(Player_count, selectors.Length));

                                        for (int i = 0; i < maxPlayers; i++)
                                        {
                                            if (int.TryParse(parts[i].Trim(), out int colourIdx) &&
                                                colourIdx >= 0 &&
                                                colourIdx < selectors[i].Items.Count)
                                            {
                                                selectors[i].SelectedIndex = colourIdx;
                                            }
                                        }
                                    }
                                    colourLine++;
                                }
                            }
                        }

                        isUpdatingUI = false;

                        UpdateBuoyDropdownItems();

                        if (Harbours_list.Count > 0) Harbours_list_view.SelectedIndex = 0;
                        else UpdateHarbourPanel();

                        if (Caves_list.Count > 0) Caves_list_view.SelectedIndex = 0;
                        else UpdateCavePanel();
                    }
                    catch (Exception ex)
                    {
                        isUpdatingUI = false;
                        MessageBox.Show("Failed to load map preset. The file might be corrupted.\n" + ex.Message, "Error Loading Map Preset", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    }
                }
            }
        }

        Random rand = new Random();

        private async void Map_export_button_Click(object sender, EventArgs e)
        {
            //Start with safety checks

            //Singular harbour is not a valid amount
            if (Harbours_list.Count == 1)
            {
                MessageBox.Show("A single harbour is not a valid amount as no connections can be established.", "Invalid Harbour Count", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            //Check if all harbours have a seleted rotation
            for (int i = 0; i < Harbours_list.Count; i++)
            {
                // Assuming -1 means no rotation is selected
                if (Harbours_list[i].rotation < 0)
                {
                    MessageBox.Show($"Harbour #{i + 1} does not have a rotation selected.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            //Check if all harbours have at least one connection
            for (int i = 0; i < Harbours_list.Count; i++)
            {
                if (Harbours_list[i].buoy_1_connection == -1 && Harbours_list[i].buoy_2_connection == -1)
                {
                    MessageBox.Show($"Harbour #{i + 1} does not have any connections.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            //Check if all caves have a selected type
            for (int i = 0; i < Caves_list.Count; i++)
            {
                // Assuming -1 means no cave type is selected
                if (Caves_list[i].type < 0)
                {
                    MessageBox.Show($"Cave #{i + 1} does not have a type selected.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            //Check if the sacrifice amount limits are not crossed
            var sacrificeChecks = new (System.Windows.Forms.ListView lv, string name, int max)[]
            {
                (Sacrifices_no_research_Bavarians, "Bavarians (No Research)", 4),
                (Sacrifices_no_research_Egyptians, "Egyptians (No Research)", 4),
                (Sacrifices_no_research_Scots, "Scots (No Research)", 4),
                (Sacrifices_research_Bavarians, "Bavarians (Research)", 8),
                (Sacrifices_research_Egyptians, "Egyptians (Research)", 8),
                (Sacrifices_research_Scots, "Scots (Research)", 8)
            };

            foreach (var (lv, name, max) in sacrificeChecks)
            {
                if (lv.CheckedItems.Count > max)
                {
                    MessageBox.Show($"Sacrifice limit exceeded for {name}. Maximum allowed is {max}.", "Sacrifice limits crossed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            //Check if 2 players don't have the same default colour
            var colourSelectors = new[]
            {
                Player_1_select, Player_2_select, Player_3_select,
                Player_4_select, Player_5_select, Player_6_select
            };

            var activeColours = colourSelectors
                .Take(Player_count)
                .Select(s => s.SelectedIndex)
                .Where(idx => idx >= 0)
                .ToList();

            if (activeColours.Count != activeColours.Distinct().Count())
            {
                MessageBox.Show("Two or more active players have been assigned the same default colour.", "Colour overlap", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            //In this case do not allow the user to proceed
            if (Map_name_button.Text.Length > 20)
            {
                MessageBox.Show("Current map name with a length of " + Map_name_button.Text.Length + " characters is larger than the maximum allowed of 20 characters", "Map name is too long", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SaveFileDialog saveFileDialog = new SaveFileDialog();

            using (saveFileDialog)
            {
                saveFileDialog.Filter = "AdK map file (*.s2m)|*.s2m|All files (*.*)|*.*";
                saveFileDialog.Title = "Export the map to AdK";
                saveFileDialog.InitialDirectory = Path.GetDirectoryName(DnG_map_path.Text);
                saveFileDialog.FileName = Path.GetFileName(DnG_map_path.Text);

                while (true)
                {
                    if (saveFileDialog.ShowDialog() != DialogResult.OK)
                    {
                        return;
                    }

                    string selectedFileName = Path.GetFileNameWithoutExtension(saveFileDialog.FileName);

                    if (Multiplayer_prefix_checkbox.Checked)
                    {
                        if (selectedFileName.Length > 15)
                        {
                            MessageBox.Show(
                                $"Current file name with a length of {selectedFileName.Length} characters is longer than the maximum allowed for multiplayer maps (15 characters).",
                                "File name is too long",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning
                            );

                            saveFileDialog.FileName = Path.GetFileName(saveFileDialog.FileName);
                            continue;
                        }
                    }
                    else
                    {
                        if (selectedFileName.Length > 20)
                        {
                            MessageBox.Show(
                                $"Current file name with a length of {selectedFileName.Length} characters is longer than the maximum allowed for singleplayer maps (20 characters).",
                                "File name is too long",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning
                            );

                            saveFileDialog.FileName = Path.GetFileName(saveFileDialog.FileName);
                            continue;
                        }
                    }

                    break;
                }
            }

            // Capture UI data on the UI thread before offloading heavy work
            int[] selectedColours = colourSelectors.Select(s => s.SelectedIndex).ToArray();
            int[] bavariansNoRes = Sacrifices_no_research_Bavarians.CheckedIndices.Cast<int>().ToArray();
            int[] bavariansRes = Sacrifices_research_Bavarians.CheckedIndices.Cast<int>().ToArray();
            int[] egyptiansNoRes = Sacrifices_no_research_Egyptians.CheckedIndices.Cast<int>().ToArray();
            int[] egyptiansRes = Sacrifices_research_Egyptians.CheckedIndices.Cast<int>().ToArray();
            int[] scotsNoRes = Sacrifices_no_research_Scots.CheckedIndices.Cast<int>().ToArray();
            int[] scotsRes = Sacrifices_research_Scots.CheckedIndices.Cast<int>().ToArray();

            Export_wait.Visible = true;
            Tab_control.Enabled = false;

            try
            {
                byte[] exportedMap = await Task.Run(() => MapExportScript(
                    selectedColours,
                    bavariansNoRes, bavariansRes,
                    egyptiansNoRes, egyptiansRes,
                    scotsNoRes, scotsRes
                ));

                if (exportedMap != null)
                {
                    string tempFile = Path.Combine(TempFolder, Path.GetFileName(saveFileDialog.FileName));
                    File.WriteAllBytes(tempFile, exportedMap);

                    if (Multiplayer_prefix_checkbox.Checked)
                    {
                        string directory = Path.GetDirectoryName(saveFileDialog.FileName);
                        string baseFileName = Path.GetFileNameWithoutExtension(saveFileDialog.FileName);
                        string extension = Path.GetExtension(saveFileDialog.FileName);

                        destinationFileName = $"MP_{Player_count}P_{baseFileName.Replace(' ', '_').ToLowerInvariant()}{extension}";
                    }
                    else
                    {
                        string directory = Path.GetDirectoryName(saveFileDialog.FileName);
                        string baseFileName = Path.GetFileNameWithoutExtension(saveFileDialog.FileName);
                        string extension = Path.GetExtension(saveFileDialog.FileName);
                        string suffix = " (1 player)";

                        destinationFileName = Path.Combine(directory, $"{baseFileName}{suffix}{extension}");
                    }
                    
                    DnG = false;
                    Compress = true;
                    sourceFileName = tempFile;

                    Archiver();
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Map export failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                Export_wait.Visible = false;
                Tab_control.Enabled = true;
            }
        }

        // Pre-parsed static inverted byte arrays for sacrifice items to avoid runtime string operations
        private static readonly byte[][] BavariansNoResearch = ParseSacrificeHex("35c4b71d", "a2f5e32d", "6fefede4", "f383de73");
        private static readonly byte[][] BavariansResearch = ParseSacrificeHex("53b934cd", "e004ee9d", "1febb7fd", "bc7b97fd", "5e280c63", "735a329d", "baabb874", "5b539fba", "323126d4", "d4f78e4d", "4b315faf", "a2ce1103", "8958e51d", "480a25f4", "7ba91903", "2c7fddfd");
        private static readonly byte[][] EgyptiansNoResearch = ParseSacrificeHex("96ab1d94", "87470603", "52c3746d", "4ac6d3c4", "b2764844");
        private static readonly byte[][] EgyptiansResearch = ParseSacrificeHex("4c23e453", "c5af4653", "00daaec4", "35b39674", "db9f1aed", "2b884d0d", "7086ed6d", "737c4144", "2000b053", "58519ab3", "6bedea64", "5a556ffa", "737c9083", "bbf37663", "6bf2dc44", "f97b6124", "0c3362ad", "0635f84d");
        private static readonly byte[][] ScotsNoResearch = ParseSacrificeHex("da50a154", "510391dd", "11e6f6b4", "26185ba4", "3625fdbd");
        private static readonly byte[][] ScotsResearch = ParseSacrificeHex("43a1346d", "dd894733", "a3977964", "7fa73f44", "8c7e4874", "703c5903", "ec961034", "ef8c23f4", "e6378a64", "3abb0cb4", "b3000463", "da0f6d93", "335a3c43", "f4b62dc4", "7e8bf323", "b2b7e8bd");

        private static byte[][] ParseSacrificeHex(params string[] hexStrings)
        {
            var result = new byte[hexStrings.Length][];
            for (int i = 0; i < hexStrings.Length; i++)
            {
                byte[] bytes = new byte[4];
                for (int j = 0; j < 4; j++)
                    bytes[j] = Convert.ToByte(hexStrings[i].Substring(j * 2, 2), 16);
                Array.Reverse(bytes);
                result[i] = bytes;
            }
            return result;
        }
        private byte[] MapExportScript(
            int[] selectedColours,
            int[] bavariansNoRes, int[] bavariansRes,
            int[] egyptiansNoRes, int[] egyptiansRes,
            int[] scotsNoRes, int[] scotsRes)
        {
            byte[] DnG_map = File.ReadAllBytes(WorkingFileName);
            int current_dng_byte = 0;
            int format_version = BitConverter.ToInt32(DnG_map, current_dng_byte);

            // Read template directly into MemoryStream
            Assembly assembly = Assembly.GetExecutingAssembly();
            MemoryStream adkStream = new MemoryStream();
            using (Stream stream = assembly.GetManifestResourceStream("DnG_AdK_Mapedit.MP_6P_snowflake.s2m"))
            {
                stream.CopyTo(adkStream);
            }

            int template_area = 110 * 110;

            //Skip to the player count byte
            current_dng_byte += 12;
            int current_adk_byte = 24;

            //Overwrite player count
            adkStream.Position = current_adk_byte;
            adkStream.Write(BitConverter.GetBytes(Player_count), 0, 4);
            current_dng_byte += 4;
            current_adk_byte += 4;

            //Overwrite start positions (template map has 6 players)
            int startPosLength = 20 * Player_count;
            ReplaceStreamBytes(adkStream, current_adk_byte, 120, DnG_map, current_dng_byte, startPosLength);
            current_adk_byte += startPosLength;
            current_dng_byte += startPosLength;

            //Owerwrite map name
            int mapNameLength = BitConverter.ToInt32(DnG_map, current_dng_byte);
            int mapNameTotalBytes = mapNameLength + 4;
            ReplaceStreamBytes(adkStream, current_adk_byte, 19, DnG_map, current_dng_byte, mapNameTotalBytes);
            current_adk_byte += mapNameTotalBytes;
            current_dng_byte += mapNameTotalBytes;

            //Overwrite map dimensions
            adkStream.Position = current_adk_byte;
            adkStream.Write(BitConverter.GetBytes(Map_size_x), 0, 4);
            adkStream.Write(BitConverter.GetBytes(Map_size_y), 0, 4);
            current_adk_byte += 8;
            current_dng_byte += 8;

            //Overwrite player types
            adkStream.Position = current_adk_byte;
            adkStream.Write(new byte[] { 0x02, 0x00, 0x00, 0x00 }, 0, 4);
            current_adk_byte += 4;

            for (int i = 2; i <= 8; i++)
            {
                byte[] typeBytes = (i > Player_count) ? new byte[] { 0, 0, 0, 0 } : new byte[] { 1, 0, 0, 0 };
                adkStream.Write(typeBytes, 0, 4);
                current_adk_byte += 4;
            }
            current_dng_byte += 32;

            //Player colours and difficulty
            for (int i = 1; i <= 8; i++)
            {
                //Skip scripted map nations
                current_adk_byte += 4;

                //Write player colours
                adkStream.Position = current_adk_byte;
                int color = (i <= Player_count) ? selectedColours[i - 1] : (i - 1);
                adkStream.Write(BitConverter.GetBytes(color), 0, 4);
                current_adk_byte += 8; // Include skipped scripted map player teams

                //Write default difficulty level (0 = weak, 1 = normal, 2 = strong)
                adkStream.Position = current_adk_byte;
                byte[] difficulty = (i == 1 || i > Player_count) ? new byte[] { 0, 0, 0, 0 } : new byte[] { 2, 0, 0, 0 };
                adkStream.Write(difficulty, 0, 4);
                current_adk_byte += 4;
            }
            current_dng_byte += 128;

            //Skip to the UUID
            current_dng_byte += 28;
            current_adk_byte += 28;
            //Overwrite UUID
            ReplaceStreamBytes(adkStream, current_adk_byte, 16, DnG_map, current_dng_byte, 16);
            current_adk_byte += 16;
            current_dng_byte += 16;

            //Skip the player names section and 4 empty bytes before it
            current_adk_byte += 100;
            if (format_version >= 8)
            {
                current_dng_byte += 100;
            }

            //Skip to the multiplayer chests section
            current_adk_byte += 4;

            //Remove the template chests section
            ReplaceStreamBytes(adkStream, current_adk_byte, 124, new byte[] { 0, 0, 0, 0 });
            current_adk_byte += 4;

            //Skip scripted map start resources and static 4 bytes before it
            current_adk_byte += 564;

            // Sacrifices Section
            MemoryStream sacrificeData = new MemoryStream();
            using (BinaryWriter writer = new BinaryWriter(sacrificeData))
            {
                writer.Write(3); // 3 nations

                // Bavarians
                writer.Write(new byte[] { 0xA3, 0x78, 0xD3, 0xB0 });
                writer.Write(bavariansNoRes.Length + bavariansRes.Length);
                foreach (int idx in bavariansNoRes) writer.Write(BavariansNoResearch[idx]);
                foreach (int idx in bavariansRes) writer.Write(BavariansResearch[idx]);

                // Egyptians
                writer.Write(new byte[] { 0x33, 0x6D, 0x01, 0xF5 });
                writer.Write(egyptiansNoRes.Length + egyptiansRes.Length);
                foreach (int idx in egyptiansNoRes) writer.Write(EgyptiansNoResearch[idx]);
                foreach (int idx in egyptiansRes) writer.Write(EgyptiansResearch[idx]);

                // Scots
                writer.Write(new byte[] { 0xA3, 0xFD, 0x7F, 0x49 });
                writer.Write(scotsNoRes.Length + scotsRes.Length);
                foreach (int idx in scotsNoRes) writer.Write(ScotsNoResearch[idx]);
                foreach (int idx in scotsRes) writer.Write(ScotsResearch[idx]);
            }

            byte[] sacBytes = sacrificeData.ToArray();
            //Overwrite sacrifices section
            ReplaceStreamBytes(adkStream, current_adk_byte, 140, sacBytes);
            current_adk_byte += sacBytes.Length;

            //Skip to ID
            current_dng_byte += 12;
            current_adk_byte += 12;
            //Overwrite ID
            ReplaceStreamBytes(adkStream, current_adk_byte, 8, DnG_map, current_dng_byte, 8);
            current_adk_byte += 8;
            current_dng_byte += 8;

            //Skip to a map size section with unknown use
            current_adk_byte += 36;
            //Overwrite map dimensions
            adkStream.Position = current_adk_byte;
            adkStream.Write(BitConverter.GetBytes(Map_size_x), 0, 4);
            adkStream.Write(BitConverter.GetBytes(Map_size_y), 0, 4);
            current_adk_byte += 8;

            //Skip to the end of the heightmap header
            current_adk_byte += 20;
            current_dng_byte = FindSequenceOffset(DnG_map, HeightsHeader, current_dng_byte);

            //Overwrite heightmap dimensions
            ReplaceStreamBytes(adkStream, current_adk_byte, 8, DnG_map, current_dng_byte, 8);
            current_adk_byte += 8;

            int heightmap_size_x = BitConverter.ToInt32(DnG_map, current_dng_byte);
            int heightmap_size_y = BitConverter.ToInt32(DnG_map, current_dng_byte + 4);
            current_dng_byte += 8;

            //Overwrite heightmap data
            int heightmap_data_length = heightmap_size_x * heightmap_size_y * 4;
            ReplaceStreamBytes(adkStream, current_adk_byte, 777924, DnG_map, current_dng_byte, heightmap_data_length); //(441*441*4)
            current_dng_byte += heightmap_data_length;

            int map_area = Map_size_x * Map_size_y;
            int[,] heightmap_logical = new int[Map_size_x, Map_size_y];

            // Create a heightmap that uses only logical coordinates
            byte[] adkBuffer = adkStream.ToArray(); // Fetch stream array buffer once

            if (Harbours_list.Count > 0)
            {
                for (int i = 0; i < map_area; i++)
                {
                    // Column-first indexing (index increases down each column y, then moves to the next column x)
                    int x_logical = i / Map_size_y;
                    int y_logical = i % Map_size_y;
                    int x_detailed = (y_logical % 2 == 0) ? (x_logical * 4) : ((x_logical * 4) + 2);
                    int y_detailed = y_logical * 4;

                    int targetIndex = x_detailed + (y_detailed * heightmap_size_x);
                    int byteOffset = current_adk_byte + (targetIndex * 4);

                    if (byteOffset >= 0 && byteOffset + 4 <= adkBuffer.Length)
                    {
                        //Swap x and y coordinates
                        heightmap_logical[y_logical, x_logical] = BitConverter.ToInt32(adkBuffer, byteOffset);
                    }
                }
            }
            current_adk_byte += heightmap_data_length;

            //Skip the textures header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Overwrite map area
            ReplaceStreamBytes(adkStream, current_adk_byte, 4, BitConverter.GetBytes(map_area));
            current_adk_byte += 4;
            current_dng_byte += 4;
            //Overwrite texture data
            int textures_array_beginning = current_adk_byte;
            int textures_data_length = map_area * 4;
            ReplaceStreamBytes(adkStream, current_adk_byte, template_area * 4, DnG_map, current_dng_byte, textures_data_length);
            current_dng_byte += textures_data_length;
            current_adk_byte += textures_data_length;

            //Skip gridstate map header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Overwrite map area
            ReplaceStreamBytes(adkStream, current_adk_byte, 4, BitConverter.GetBytes(map_area));
            current_adk_byte += 4;
            current_dng_byte += 4;
            //Overwrite gridstate data (length should be the same as the texture data)
            int gridstate_array_beginning = current_adk_byte;
            ReplaceStreamBytes(adkStream, current_adk_byte, template_area * 4, DnG_map, current_dng_byte, textures_data_length);
            current_dng_byte += textures_data_length;

            adkBuffer = adkStream.ToArray();

            // Strip invalid in AdK harbour flags (byte 2, 0x10) from initial gridstate
            int gridstateOffsetTemp = gridstate_array_beginning;
            for (int i = 0; i < map_area; i++)
            {
                gridstateOffsetTemp += 1; // Byte 2
                adkBuffer[gridstateOffsetTemp] &= 0xEF;
                gridstateOffsetTemp += 3; // Advance to next tile Byte 1
            }

            // Block hexagons occupied by caves (byte 2, 0x04)
            foreach (var cave in Caves_list)
            {
                int caveByteIndex = gridstate_array_beginning + (cave.pos_y * Map_size_x + cave.pos_x) * 4 + 1;
                adkBuffer[caveByteIndex] |= 0x04;
            }

            // Texture Swapping Loop
            foreach (var swap in Swap_list)
            {
                if (swap.tab == 1)
                {
                    int textureTypeFrom = DnG_texture_types[swap.from];
                    byte[] textureTo = (byte[])AdK_textures[swap.to].Clone();
                    int textureTypeTo = AdK_texture_types[swap.to];

                    for (int j = 0; j < map_area; j++)
                    {
                        int textureOffset = textures_array_beginning + j * 4;
                        if (BitConverter.ToInt32(adkBuffer, textureOffset) == textureTypeFrom)
                        {
                            Buffer.BlockCopy(textureTo, 0, adkBuffer, textureOffset, 4);
                            int gridstateOffset = gridstate_array_beginning + j * 4;

                            switch (textureTypeFrom)
                            {
                                case 1: adkBuffer[gridstateOffset + 1] &= 0xFD; break; // Clear Building Spot
                                case 2: adkBuffer[gridstateOffset + 0] &= 0xEF; break; // Clear Mining Spot
                                case 4:
                                    if ((adkBuffer[gridstateOffset + 0] & 0x80) == 0)
                                        adkBuffer[gridstateOffset + 0] &= 0xFE; // Clear Blocked
                                    break;
                            }

                            switch (textureTypeTo)
                            {
                                case 1: adkBuffer[gridstateOffset + 1] |= 0x02; break; // Set Building Spot
                                case 2: adkBuffer[gridstateOffset + 0] |= 0x10; break; // Set Mining Spot
                                case 4: adkBuffer[gridstateOffset + 0] |= 0x01; break; // Set Blocked
                            }
                        }
                    }
                }
            }

            // --- ANCHORAGE TEXTURE & GRIDSTATE CODE ---
            byte[] pavementTexture = new byte[] { 0x01, 0xDE, 0xCA, 0xDE };

            for (int i = 0; i < Harbours_list.Count; i++)
            {
                if (Harbours_list[i].anchorage)
                {
                    int anchorIndex = Harbours_list[i].anchor_y * Map_size_x + Harbours_list[i].anchor_x;

                    //Replace a texture under the anchorage
                    int anchorTextureOffset = textures_array_beginning + (anchorIndex * 4);
                    Buffer.BlockCopy(pavementTexture, 0, adkBuffer, anchorTextureOffset, 4);

                    // 2. Validate coastal placement & apply Anchorage Flags to Gridstate Array
                    int anchorGridstateOffset = gridstate_array_beginning + (anchorIndex * 4);

                    if ((adkBuffer[anchorGridstateOffset] & 0x08) == 0)
                    {
                        MessageBox.Show($"Harbour at index {i} has an anchor in an invalid location.", "Anchor is in invalid location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return null;
                    }

                    adkBuffer[anchorGridstateOffset + 0] = 0x08; // Coastal Terrain
                    adkBuffer[anchorGridstateOffset + 1] = 0x00; // Base
                    adkBuffer[anchorGridstateOffset + 2] = 0x02; // Anchor Point Flag
                    adkBuffer[anchorGridstateOffset + 3] = 0x00; // Reserved
                }
            }

            adkStream.Write(adkBuffer, 0, adkBuffer.Length);

            current_adk_byte = gridstate_array_beginning + textures_data_length;

            //Skip resource map header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Overwrite map dimensions
            ReplaceStreamBytes(adkStream, current_adk_byte, 8, BitConverter.GetBytes(Map_size_x));
            ReplaceStreamBytes(adkStream, current_adk_byte + 4, 0, BitConverter.GetBytes(Map_size_y));
            current_adk_byte += 8; current_dng_byte += 8;
            //Overwrite resources array
            int resources_data_length = map_area * 8;
            ReplaceStreamBytes(adkStream, current_adk_byte, template_area * 8, DnG_map, current_dng_byte, resources_data_length);
            current_dng_byte += resources_data_length;
            current_adk_byte += resources_data_length;

            //Skip territory map header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Overwrite map dimensions
            ReplaceStreamBytes(adkStream, current_adk_byte, 8, BitConverter.GetBytes(Map_size_x));
            ReplaceStreamBytes(adkStream, current_adk_byte + 4, 0, BitConverter.GetBytes(Map_size_y));
            current_adk_byte += 8; current_dng_byte += 8;
            //Overwrite territory map data (length should be the same as the texture data)
            ReplaceStreamBytes(adkStream, current_adk_byte, template_area * 4, DnG_map, current_dng_byte, textures_data_length);
            current_dng_byte += textures_data_length;
            current_adk_byte += textures_data_length;

            //Skip exploration map header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Overwrite map dimensions
            ReplaceStreamBytes(adkStream, current_adk_byte, 8, BitConverter.GetBytes(Map_size_x));
            ReplaceStreamBytes(adkStream, current_adk_byte + 4, 0, BitConverter.GetBytes(Map_size_y));
            current_adk_byte += 8; current_dng_byte += 8;
            //Overwrite exploration map data
            int exploration_map_length = map_area * 32;
            ReplaceStreamBytes(adkStream, current_adk_byte, template_area * 32, DnG_map, current_dng_byte, exploration_map_length);
            current_dng_byte += exploration_map_length;
            current_adk_byte += exploration_map_length;

            //For now just overwrite the continents map without modifing the source
            byte[] depositsHeaderDng = new byte[] { 0x04, 0x00, 0x00, 0x00, 0xAE, 0xEB, 0x66, 0xEF, 0x09, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
            byte[] depositsHeaderAdk = new byte[] { 0x06, 0x00, 0x00, 0x00, 0xAE, 0xEB, 0x66, 0xEF, 0x09, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };

            adkBuffer = adkStream.ToArray();
            int depositsOffsetDng = FindSequenceOffset(DnG_map, depositsHeaderDng, current_dng_byte);
            int depositsOffsetAdk = FindSequenceOffset(adkBuffer, depositsHeaderAdk, current_adk_byte);

            int to_copy_length = depositsOffsetDng - current_dng_byte;
            ReplaceStreamBytes(adkStream, current_adk_byte, depositsOffsetAdk - current_adk_byte, DnG_map, current_dng_byte, to_copy_length);
            current_dng_byte = depositsOffsetDng;
            current_adk_byte += to_copy_length;

            //Overwrite deposits array length
            int depositsArrayLength = BitConverter.ToInt32(DnG_map, current_dng_byte);
            current_dng_byte += 4;
            int depositsArrayLengthAdk = BitConverter.ToInt32(adkStream.ToArray(), current_adk_byte);
            ReplaceStreamBytes(adkStream, current_adk_byte, 4, BitConverter.GetBytes(depositsArrayLength));
            current_adk_byte += 4;
            //Overwrite the deposits array data
            int depositsDataLength = depositsArrayLength * 108;
            ReplaceStreamBytes(adkStream, current_adk_byte, depositsArrayLengthAdk * 108, DnG_map, current_dng_byte, depositsDataLength);
            current_dng_byte += depositsDataLength;
            current_adk_byte += depositsDataLength;

            //For now just overwrite the animals array without modifing the source
            byte[] doodadsHeader = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x3C, 0xCC, 0xBC, 0x8E, 0x0D, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
            adkBuffer = adkStream.ToArray();
            int doodadsOffsetDng = FindSequenceOffset(DnG_map, doodadsHeader, current_dng_byte);
            int doodadsOffsetAdk = FindSequenceOffset(adkBuffer, doodadsHeader, current_adk_byte);

            to_copy_length = doodadsOffsetDng - current_dng_byte;
            ReplaceStreamBytes(adkStream, current_adk_byte, doodadsOffsetAdk - current_adk_byte, DnG_map, current_dng_byte, to_copy_length);
            current_dng_byte = doodadsOffsetDng;
            current_adk_byte += to_copy_length;

            /*
            //Copy the animals array length
            int animals_array_length = BitConverter.ToInt32(DnG_map, current_dng_byte);
            current_dng_byte += 4;
            int animals_array_length_adk = BitConverter.ToInt32(AdK_map_resizable.ToArray(), current_adk_byte);
            AdK_map_resizable.RemoveRange(current_adk_byte, 4);
            AdK_map_resizable.InsertRange(current_adk_byte, BitConverter.GetBytes(animals_array_length));
            current_adk_byte += 4;
            //Copy the animals array to the AdK
            int animals_array_data_length_adk = animals_array_length_adk * 248;
            AdK_map_resizable.RemoveRange(current_adk_byte, animals_array_data_length_adk);
            int animals_array_data_length = animals_array_length * 244;
            AdK_map_resizable.InsertRange(current_adk_byte, DnG_map.Skip(current_dng_byte).Take(animals_array_data_length));
            current_dng_byte += animals_array_data_length;
            //Update the animals array
            for (int i = 0; i < animals_array_length; i++)
            {
                //Skip the type
                current_adk_byte += 4;
                //Change the format version
                AdK_map_resizable[current_adk_byte] = 0x03;
                //Skip to the end of the entry in the array
                current_adk_byte += 240;
                //Insert 4 empty bytes at the end of the entry
                AdK_map_resizable.InsertRange(current_adk_byte, new byte[] { 0x00, 0x00, 0x00, 0x00 });
                current_adk_byte += 4;
            }

            //Skip doodads header
            current_adk_byte += 28;
            //Skip the unused in AdK animal respawn array
            byte[] doodads_array_header = new byte[] { 0x00, 0x00, 0x00, 0x00, 0x3C, 0xCC, 0xBC, 0x8E, 0x0D, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
            current_dng_byte = FindSequenceOffset(DnG_map, doodads_array_header, current_dng_byte);
            */

            //Overwrite doodads array length
            int doodadsArrayLength = BitConverter.ToInt32(DnG_map, current_dng_byte);
            current_dng_byte += 4;
            int doodadsArrayLengthAdk = BitConverter.ToInt32(adkStream.ToArray(), current_adk_byte);

            int anchoragesAmount = Harbours_list.Count(h => h.anchorage);
            ReplaceStreamBytes(adkStream, current_adk_byte, 4, BitConverter.GetBytes(doodadsArrayLength + anchoragesAmount));
            current_adk_byte += 4;
            //Overwrite doodads array data
            int doodadsDataLength = doodadsArrayLength * 56;
            ReplaceStreamBytes(adkStream, current_adk_byte, doodadsArrayLengthAdk * 56, DnG_map, current_dng_byte, doodadsDataLength);
            current_dng_byte += doodadsDataLength;
            current_adk_byte += doodadsDataLength;

            //Add anchor doodads
            foreach (var harbour in Harbours_list)
            {
                if (harbour.anchorage)
                {
                    MemoryStream anchorDoodad = new MemoryStream();
                    using (BinaryWriter w = new BinaryWriter(anchorDoodad))
                    {
                        w.Write(new byte[] { 0x00, 0x1b, 0xff, 0xf1 });
                        w.Write(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x5B, 0x76, 0x5C, 0xEF, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00 });
                        w.Write(GenerateUniqueId());
                        w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1D, 0x85, 0x47, 0x6F, 0x0F, 0x00, 0x00, 0x00 });

                        int anchor_x = (harbour.anchor_y % 2 == 0) ? harbour.anchor_x * 4 : (harbour.anchor_x * 4) + 2;
                        w.Write(anchor_x);
                        w.Write(harbour.anchor_y * 4);
                    }
                    byte[] anchorBytes = anchorDoodad.ToArray();
                    ReplaceStreamBytes(adkStream, current_adk_byte, 0, anchorBytes);
                    current_adk_byte += anchorBytes.Length;
                }
            }

            if (BitConverter.ToInt32(DnG_map, current_dng_byte) != 0)
            {
                MessageBox.Show("Lifetime doodad spotted, send this map for an inspection to the developer.", "Lifetime doodad spotted", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return null;
            }
            current_dng_byte += 4;
            current_adk_byte += 4;

            //Overwrite blocking doodads array length
            int blockingDoodadsLength = BitConverter.ToInt32(DnG_map, current_dng_byte);
            current_dng_byte += 4;
            int blockingDoodadsLengthAdk = BitConverter.ToInt32(adkStream.ToArray(), current_adk_byte);
            ReplaceStreamBytes(adkStream, current_adk_byte, 4, BitConverter.GetBytes(blockingDoodadsLength));
            current_adk_byte += 4;
            //Overwrite blocking doodads array data
            int blockingDoodadsDataLength = blockingDoodadsLength * 56;
            ReplaceStreamBytes(adkStream, current_adk_byte, blockingDoodadsLengthAdk * 56, DnG_map, current_dng_byte, blockingDoodadsDataLength);
            current_dng_byte += blockingDoodadsDataLength;
            current_adk_byte += blockingDoodadsDataLength;

            //Skip ambients header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Read the ambients array length (template map has no ambients)
            int ambientsLength = BitConverter.ToInt32(DnG_map, current_dng_byte);
            current_dng_byte += 4;
            ReplaceStreamBytes(adkStream, current_adk_byte, 4, BitConverter.GetBytes(ambientsLength));
            current_adk_byte += 4;
            //Copy the ambients array data to the AdK map
            int ambientsDataLength = ambientsLength * 24;
            ReplaceStreamBytes(adkStream, current_adk_byte, 0, DnG_map, current_dng_byte, ambientsDataLength);
            current_adk_byte += ambientsDataLength;
            //End of the DnG map, no need to update current_dng_byte anymore
            current_adk_byte += ambientsDataLength;

            //Skip buoy connections header
            current_adk_byte += 36;
            //Generate buoy connections and harbour IDs
            GenerateBuoyConnections();
            //Write buoy connections amount (template map has none)
            ReplaceStreamBytes(adkStream, current_adk_byte, 4, BitConverter.GetBytes(Buoy_connections.Count));
            current_adk_byte += 4;

            //Write buoy connections
            if (Harbours_list.Count > 0)
            {
                foreach (var connection in Buoy_connections)
                {
                    MemoryStream buoyStream = new MemoryStream();
                    using (BinaryWriter w = new BinaryWriter(buoyStream))
                    {
                        //Write the first static value and the ID header
                        w.Write(new byte[] { 0x01, 0x00, 0x00, 0x00, 0x2D, 0xD1, 0x27, 0x1C, 0x0E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00 });
                        //Write the connection ID
                        w.Write(connection.connection_id);
                        w.Write(0);
                        //Write the ID header
                        w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00 });
                        //Write the first harbour ID
                        w.Write(connection.harbour_1_id);
                        w.Write(0);
                        //Write the ID header
                        w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00 });
                        //Write the second harbour ID
                        w.Write(connection.harbour_2_id);
                        w.Write(0);
                        //Write the third static value
                        w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x79, 0x3C, 0xF8, 0x25, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
                    }

                    byte[] bBytes = buoyStream.ToArray();
                    ReplaceStreamBytes(adkStream, current_adk_byte, 0, bBytes);
                    current_adk_byte += bBytes.Length;

                    //Compute the path connecting the buoys
                    int[][] buoyPath = FindPath(heightmap_logical, new[] { connection.buoy_source_x, connection.buoy_source_y }, new[] { connection.buoy_target_x, connection.buoy_target_y }, globalReservedPaths);

                    if (buoyPath != null)
                    {
                        ReplaceStreamBytes(adkStream, current_adk_byte, 0, BitConverter.GetBytes(buoyPath.Length));
                        current_adk_byte += 4;

                        foreach (int[] step in buoyPath)
                        {
                            MemoryStream stepMs = new MemoryStream();
                            using (BinaryWriter w = new BinaryWriter(stepMs))
                            {
                                //PatternCursor
                                w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00 });
                                //X
                                w.Write(step[0]);
                                //Y
                                w.Write(step[1]);
                            }
                            byte[] sBytes = stepMs.ToArray();
                            ReplaceStreamBytes(adkStream, current_adk_byte, 0, sBytes);
                            current_adk_byte += sBytes.Length;
                            globalReservedPaths.Add(step);
                        }
                    }
                    else
                    {
                        MessageBox.Show("Path connecting buoys (implement) is blocked.", "Path can't be established", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        return null;
                    }
                }
            }

            //Skip harbours data header
            current_adk_byte += 16;

            //Write harbours amount (template map has none)
            ReplaceStreamBytes(adkStream, current_adk_byte, 4, BitConverter.GetBytes(Harbours_list.Count));
            current_adk_byte += 4;

            //Write harbour data, 404 bytes per harbour
            for (int i = 0; i < Harbours_list.Count; i++)
            {
                var harbour = Harbours_list[i];
                int harbour_rotation = harbour.rotation;

                MemoryStream harbourStream = new MemoryStream();
                using (BinaryWriter w = new BinaryWriter(harbourStream))
                {
                    //Write harbour rotation
                    w.Write((byte[])HarbourRotations[harbour_rotation].Clone());

                    w.Write(new byte[] { 0x02, 0x00, 0x00, 0x00, 0x0F, 0xA9, 0xE5, 0x3E, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00 });

                    //Write a harbour ID
                    w.Write(Harbour_data[i].harbour_id);

                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00 });

                    //Write harbour flag position
                    w.Write(harbour.pos_x);
                    w.Write(harbour.pos_y);

                    //Set the mystery value to 2 in order to skip creation of a separate array storing harbour IDs
                    w.Write(2);

                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x7F, 0x63, 0xCD, 0xE0, 0x13, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x87, 0x07, 0xFF, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00 });

                    //Write buoy 1 docking position 1
                    var dOffset = buoy1_docking_positions[harbour_rotation, 0];
                    w.Write(harbour.pos_x + dOffset.offsetX);
                    w.Write(harbour.pos_y + dOffset.offsetY);

                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x20, 0x87, 0x07, 0xFF, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00 });

                    //Write buoy 1 docking position 2
                    dOffset = buoy1_docking_positions[harbour_rotation, 1];
                    w.Write(harbour.pos_x + dOffset.offsetX);
                    w.Write(harbour.pos_y + dOffset.offsetY);

                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00 });

                    //Write buoy 1 connection ID
                    if (harbour.buoy_1_connection <= 0)
                        w.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
                    else
                    {
                        w.Write(Harbour_data[i].buoy_1_connection_id);
                        w.Write(0);
                    }

                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00 });

                    //Write buoy 1 world position
                    var buoyCoords = GetBuoyWorldCoordinates(harbour, 0);
                    w.Write(buoyCoords.x);
                    w.Write(buoyCoords.y);

                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x7F, 0x63, 0xCD, 0xE0, 0x13, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x87, 0x07, 0xFF, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00 });

                    //Write buoy 2 docking position 1
                    dOffset = buoy2_docking_positions[harbour_rotation, 0];
                    w.Write(harbour.pos_x + dOffset.offsetX);
                    w.Write(harbour.pos_y + dOffset.offsetY);

                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x20, 0x87, 0x07, 0xFF, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00 });

                    //Write buoy 2 docking position 2
                    dOffset = buoy2_docking_positions[harbour_rotation, 1];
                    w.Write(harbour.pos_x + dOffset.offsetX);
                    w.Write(harbour.pos_y + dOffset.offsetY);

                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00 });

                    //Write buoy 2 connection ID
                    if (harbour.buoy_2_connection <= 0)
                        w.Write(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
                    else
                    {
                        w.Write(Harbour_data[i].buoy_2_connection_id);
                        w.Write(0);
                    }

                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00 });

                    // Write buoy 2 world position
                    var buoy2Coords = GetBuoyWorldCoordinates(harbour, 1);
                    w.Write(buoy2Coords.x);
                    w.Write(buoy2Coords.y);

                    w.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF });
                }

                byte[] harbourBytes = harbourStream.ToArray();
                ReplaceStreamBytes(adkStream, current_adk_byte, 0, harbourBytes);
                current_adk_byte += harbourBytes.Length;
            }

            //Skip caves data header
            current_adk_byte += 16;
            //Wipe template caves data (end of the file)
            adkStream.SetLength(current_adk_byte);
            
            using (BinaryWriter writer = new BinaryWriter(adkStream))
            {
                //Write the caves data to the AdK map
                writer.Write(Caves_list.Count);
                foreach (var cave in Caves_list)
                {
                    writer.Write((byte[])CaveTypes[cave.type].Clone());
                    writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x74, 0x76, 0x80, 0x4A, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00 });
                    writer.Write(GenerateUniqueId());
                    writer.Write(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00 });
                    writer.Write(cave.pos_x);
                    writer.Write(cave.pos_y);
                }
                //Add empty 4 bytes at the end of the file
                writer.Write(0);
            }

            //Remove water sign doodad with no texture (currently waiting for the map with a lifetime doodad to appear)

            return adkStream.ToArray();
        }

        // MemoryStream Byte Replacement Helper Method
        private static void ReplaceStreamBytes(MemoryStream stream, int position, int removeCount, byte[] insertBytes, int srcOffset = 0, int insertCount = -1)
        {
            if (insertCount < 0) insertCount = insertBytes.Length;

            if (removeCount == insertCount)
            {
                // Direct overwrite - Fast O(1)
                stream.Position = position;
                stream.Write(insertBytes, srcOffset, insertCount);
            }
            else
            {
                // Resizing Stream - Slice existing content and splice new data
                byte[] buffer = stream.ToArray();
                stream.SetLength(0);
                stream.Write(buffer, 0, position);
                stream.Write(insertBytes, srcOffset, insertCount);

                int remainingOffset = position + removeCount;
                if (remainingOffset < buffer.Length)
                {
                    stream.Write(buffer, remainingOffset, buffer.Length - remainingOffset);
                }
            }
        }

        // List of all used IDs
        HashSet<int> used_IDs = new HashSet<int>();

        // Offset lookup table: [rotationIndex, buoyIndex] -> (dx, dy)
        // Rotation mapping: 0=SW, 1=NW, 2=SE, 3=NE, 4=N, 5=S, 6=E, 7=W
        private static readonly (int dx, int dy)[,] BuoyOffsets = new (int dx, int dy)[8, 2]
        {
    { (-2, -3), (-3, -5) }, // 0: harbor_sw
    { (-2,  3), (-3,  5) }, // 1: harbor_nw
    { ( 2, -3), ( 3, -5) }, // 2: harbor_se
    { ( 2,  3), ( 3,  5) }, // 3: harbor_ne
    { ( 0,  3), ( 0,  5) }, // 4: harbor_n
    { ( 0, -3), ( 0, -5) }, // 5: harbor_s
    { ( 3,  0), ( 5,  0) }, // 6: harbor_e
    { (-3,  0), (-5,  0) }  // 7: harbor_w
        };

        // Generated results
        public List<(int connection_id, int harbour_1_id, int harbour_2_id, int buoy_source_x, int buoy_source_y, int buoy_target_x, int buoy_target_y)> Buoy_connections
            = new List<(int, int, int, int, int, int, int)>();

        public List<(int harbour_id, int buoy_1_connection_id, int buoy_2_connection_id)> Harbour_data
            = new List<(int, int, int)>();

        /// <summary>
        /// Generates a unique random integer ID within range and tracks it in used_IDs.
        /// </summary>
        private int GenerateUniqueId()
        {
            int id;
            do
            {
                id = rand.Next(1000000, int.MaxValue);
            }
            while (used_IDs.Contains(id));

            used_IDs.Add(id);
            return id;
        }

        public void GenerateBuoyConnections()
        {
            Buoy_connections.Clear();
            Harbour_data.Clear();
            globalReservedPaths.Clear();

            var processedPairs = new HashSet<(int, int)>();

            // 1. Generate unique random IDs for all harbours upfront
            int[] harbourIds = new int[Harbours_list.Count];
            for (int i = 0; i < Harbours_list.Count; i++)
            {
                harbourIds[i] = GenerateUniqueId();
            }

            // 2D array to track connection IDs per harbour buoy [harborIndex, buoySubIndex]
            int[,] harbourBuoyConnectionIds = new int[Harbours_list.Count, 2];

            for (int i = 0; i < Harbours_list.Count; i++)
            {
                var harbor = Harbours_list[i];

                // Process Buoy 1 (Sub-index 0)
                ProcessConnection(i, 0, harbor.buoy_1_connection, processedPairs, harbourBuoyConnectionIds, harbourIds);

                // Process Buoy 2 (Sub-index 1)
                ProcessConnection(i, 1, harbor.buoy_2_connection, processedPairs, harbourBuoyConnectionIds, harbourIds);
            }

            // 2. Populate Harbour_data using the generated harbour IDs
            for (int i = 0; i < Harbours_list.Count; i++)
            {
                Harbour_data.Add((
                    harbourIds[i],
                    harbourBuoyConnectionIds[i, 0],
                    harbourBuoyConnectionIds[i, 1]
                ));
            }
        }

        /// <summary>
        /// Calculates world coordinates for a specific buoy on a harbor.
        /// </summary>
        public (int x, int y) GetBuoyWorldCoordinates(
            (int pos_x, int pos_y, int rotation, bool anchorage, int anchor_x, int anchor_y, int buoy_1_connection, int buoy_2_connection) harbor,
            int buoySubIndex)
        {
            if (harbor.rotation < 0 || harbor.rotation > 7)
                throw new ArgumentOutOfRangeException(nameof(harbor.rotation), "Rotation index must be between 0 and 7.");

            var (offset_x, offset_y) = BuoyOffsets[harbor.rotation, buoySubIndex];
            return (harbor.pos_x + offset_x, harbor.pos_y + offset_y);
        }

        private void ProcessConnection(
            int sourceHarborIdx,
            int sourceBuoySubIdx,
            int targetBuoyId,
            HashSet<(int, int)> processedPairs,
            int[,] harbourBuoyConnectionIds,
            int[] harbourIds)
        {
            int maxBuoyId = Harbours_list.Count * 2;

            // Skip connection if target buoy ID is <= 0 (0 = no connection, -1 = invalid) or out of range
            if (targetBuoyId <= 0 || targetBuoyId > maxBuoyId)
                return;

            // Calculate 1-based unique ID for the source buoy
            int sourceBuoyId = (sourceHarborIdx * 2) + sourceBuoySubIdx + 1;

            // Prevent duplicate bidirectional connections
            int minId = Math.Min(sourceBuoyId, targetBuoyId);
            int maxId = Math.Max(sourceBuoyId, targetBuoyId);
            if (!processedPairs.Add((minId, maxId)))
                return;

            // Convert 1-based target buoy ID back to Target Harbor Index and Target Buoy Sub-index
            int targetHarborIdx = (targetBuoyId - 1) / 2;
            int targetBuoySubIdx = (targetBuoyId - 1) % 2;

            var sourceHarbor = Harbours_list[sourceHarborIdx];
            var targetHarbor = Harbours_list[targetHarborIdx];

            // Get coordinates for both buoy positions
            var source_coordinates = GetBuoyWorldCoordinates(sourceHarbor, sourceBuoySubIdx);
            var target_coordinates = GetBuoyWorldCoordinates(targetHarbor, targetBuoySubIdx);

            // Generate a random unique ID for the connection
            int connectionId = GenerateUniqueId();

            // Add connection record using generated harbour IDs instead of array indices
            Buoy_connections.Add((
                connectionId,
                harbourIds[sourceHarborIdx],
                harbourIds[targetHarborIdx],
                source_coordinates.x,
                source_coordinates.y,
                target_coordinates.x,
                target_coordinates.y
            ));

            // Map connection ID to both source and target buoy slots
            harbourBuoyConnectionIds[sourceHarborIdx, sourceBuoySubIdx] = connectionId;
            harbourBuoyConnectionIds[targetHarborIdx, targetBuoySubIdx] = connectionId;
        }

        List<int[]> globalReservedPaths = new List<int[]>();

        public struct State : IEquatable<State>
        {
            public int Row { get; }
            public int Col { get; }
            public int Direction { get; }

            public State(int row, int col, int direction)
            {
                Row = row;
                Col = col;
                Direction = direction;
            }

            public bool Equals(State other)
            {
                return Row == other.Row && Col == other.Col && Direction == other.Direction;
            }

            public override bool Equals(object obj)
            {
                return obj is State other && Equals(other);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = 17;
                    hash = hash * 31 + Row;
                    hash = hash * 31 + Col;
                    hash = hash * 31 + Direction;
                    return hash;
                }
            }
        }

        private class Node
        {
            public State State { get; }
            public int GCost { get; }
            public int HCost { get; }
            public int FCost => GCost + HCost;
            public Node Parent { get; }

            public Node(State state, int gCost, int hCost, Node parent = null)
            {
                State = state;
                GCost = gCost;
                HCost = hCost;
                Parent = parent;
            }
        }

        // Direction offsets for Odd-R grid (0: E, 1: SE, 2: SW, 3: W, 4: NW, 5: NE)
        private static readonly int[][][] Offsets = new int[][][]
        {
        // Even Rows (y % 2 == 0)
        new int[][] {
            new int[] { 0, 1 },  // 0: East
            new int[] { 1, 0 },  // 1: SE
            new int[] { 1, -1 }, // 2: SW
            new int[] { 0, -1 }, // 3: West
            new int[] { -1, -1 },// 4: NW
            new int[] { -1, 0 }  // 5: NE
        },
        // Odd Rows (y % 2 != 0) - Shifted Right
        new int[][] {
            new int[] { 0, 1 },  // 0: East
            new int[] { 1, 1 },  // 1: SE
            new int[] { 1, 0 },  // 2: SW
            new int[] { 0, -1 }, // 3: West
            new int[] { -1, 0 }, // 4: NW
            new int[] { -1, 1 }  // 5: NE
        }
        };

        /// <summary>
        /// Finds the optimal path from start to goal as an array of [x, y] coordinates.
        /// </summary>
        /// <param name="heightMap">2D array [row, col] of heights as signed integers.</param>
        /// <param name="start">Start coordinate array [x, y].</param>
        /// <param name="goal">Goal coordinate array [x, y].</param>
        /// <param name="establishedPaths">List/collection of previously computed paths to treat as impassable.</param>
        /// <returns>Array of [x, y] coordinates from start to goal, or null if no valid path exists.</returns>
        public static int[][] FindPath(
            int[,] heightMap,
            int[] start,
            int[] goal,
            IEnumerable<int[]> establishedPaths = null)
        {
            int maxRows = heightMap.GetLength(0);
            int maxCols = heightMap.GetLength(1);

            int startCol = start[0], startRow = start[1];
            int goalCol = goal[0], goalRow = goal[1];

            // Store already reserved coordinates for O(1) lookup
            HashSet<Tuple<int, int>> blockedCoordinates = new HashSet<Tuple<int, int>>();
            if (establishedPaths != null)
            {
                foreach (var coord in establishedPaths)
                {
                    if (coord != null && coord.Length >= 2)
                    {
                        blockedCoordinates.Add(Tuple.Create(coord[1], coord[0])); // y = Row, x = Col
                    }
                }
            }

            // Validate start/goal bounds, height, and existing path overlaps
            if (!IsValid(startRow, startCol, maxRows, maxCols) ||
                !IsValid(goalRow, goalCol, maxRows, maxCols) ||
                heightMap[startRow, startCol] >= -100 ||
                heightMap[goalRow, goalCol] >= -100 ||
                blockedCoordinates.Contains(Tuple.Create(startRow, startCol)) ||
                blockedCoordinates.Contains(Tuple.Create(goalRow, goalCol)))
            {
                return null;
            }

            MinHeapPriorityQueue<Node> openSet = new MinHeapPriorityQueue<Node>();
            Dictionary<State, int> gCosts = new Dictionary<State, int>();

            State startState = new State(startRow, startCol, -1);
            Node startNode = new Node(startState, 0, GetHeuristic(startRow, startCol, goalRow, goalCol));

            openSet.Enqueue(startNode, startNode.FCost);
            gCosts[startState] = 0;

            Node bestGoalNode = null;

            while (openSet.Count > 0)
            {
                Node current = openSet.Dequeue();

                if (current.State.Row == goalRow && current.State.Col == goalCol)
                {
                    bestGoalNode = current;
                    break;
                }

                int curRow = current.State.Row;
                int curCol = current.State.Col;
                int parity = Math.Abs(curRow % 2);

                for (int dir = 0; dir < 6; dir++)
                {
                    int nextRow = curRow + Offsets[parity][dir][0];
                    int nextCol = curCol + Offsets[parity][dir][1];

                    // Check bounds, land impassability (>= -100), and established path collisions
                    if (!IsValid(nextRow, nextCol, maxRows, maxCols) ||
                        heightMap[nextRow, nextCol] >= -100 ||
                        blockedCoordinates.Contains(Tuple.Create(nextRow, nextCol)))
                    {
                        continue;
                    }

                    // 1. Base Cost
                    int stepCost = 1;

                    // 2. Penalty: Water depth >= -4000
                    if (heightMap[nextRow, nextCol] >= -4000)
                        stepCost += 1;

                    // 3. Penalty: Entering water adjacent to land (>= -100)
                    if (IsNearLand(heightMap, nextRow, nextCol, maxRows, maxCols))
                        stepCost += 1;

                    // 4. Penalty: Turning (changing direction)
                    if (current.State.Direction != -1 && current.State.Direction != dir)
                        stepCost += 1;

                    int newGCost = current.GCost + stepCost;
                    State nextState = new State(nextRow, nextCol, dir);

                    int existingG;
                    if (!gCosts.TryGetValue(nextState, out existingG) || newGCost < existingG)
                    {
                        gCosts[nextState] = newGCost;
                        int hCost = GetHeuristic(nextRow, nextCol, goalRow, goalCol);
                        Node neighborNode = new Node(nextState, newGCost, hCost, current);
                        openSet.Enqueue(neighborNode, neighborNode.FCost);
                    }
                }
            }

            if (bestGoalNode == null) return null;

            // Reconstruct path to int[][] array of [x, y] coordinates
            List<int[]> pathList = new List<int[]>();
            Node curr = bestGoalNode;
            while (curr != null)
            {
                pathList.Add(new int[] { curr.State.Col, curr.State.Row }); // [x, y]
                curr = curr.Parent;
            }

            pathList.Reverse();
            return pathList.ToArray();
        }

        private static bool IsNearLand(int[,] map, int r, int c, int maxR, int maxC)
        {
            int parity = Math.Abs(r % 2);
            for (int i = 0; i < 6; i++)
            {
                int nr = r + Offsets[parity][i][0];
                int nc = c + Offsets[parity][i][1];
                if (IsValid(nr, nc, maxR, maxC) && map[nr, nc] >= -100)
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsValid(int r, int c, int maxR, int maxC)
        {
            return r >= 0 && r < maxR && c >= 0 && c < maxC;
        }

        private static int GetHeuristic(int r1, int c1, int r2, int c2)
        {
            int q1 = c1 - (r1 - (r1 & 1)) / 2;
            int s1 = -q1 - r1;

            int q2 = c2 - (r2 - (r2 & 1)) / 2;
            int s2 = -q2 - r2;

            return (Math.Abs(q1 - q2) + Math.Abs(r1 - r2) + Math.Abs(s1 - s2)) / 2;
        }

        // Min-heap Binary Priority Queue implementation for .NET 4.8
        private class MinHeapPriorityQueue<T>
        {
            private readonly List<Tuple<T, int>> elements = new List<Tuple<T, int>>();

            public int Count => elements.Count;

            public void Enqueue(T item, int priority)
            {
                elements.Add(Tuple.Create(item, priority));
                int ci = elements.Count - 1;
                while (ci > 0)
                {
                    int pi = (ci - 1) / 2;
                    if (elements[ci].Item2 >= elements[pi].Item2) break;
                    var tmp = elements[ci]; elements[ci] = elements[pi]; elements[pi] = tmp;
                    ci = pi;
                }
            }

            public T Dequeue()
            {
                int li = elements.Count - 1;
                T frontItem = elements[0].Item1;
                elements[0] = elements[li];
                elements.RemoveAt(li);

                --li;
                int pi = 0;
                while (true)
                {
                    int ci = pi * 2 + 1;
                    if (ci > li) break;
                    int rc = ci + 1;
                    if (rc <= li && elements[rc].Item2 < elements[ci].Item2)
                        ci = rc;
                    if (elements[pi].Item2 <= elements[ci].Item2) break;
                    var tmp = elements[pi]; elements[pi] = elements[ci]; elements[ci] = tmp;
                    pi = ci;
                }
                return frontItem;
            }
        }

        private static readonly byte[][] HarbourRotations = new byte[][]
{
    new byte[] { 0x85, 0x19, 0xAA, 0xAA }, // 0: South-west
    new byte[] { 0x03, 0x69, 0xB8, 0xF7 }, // 1: North-west
    new byte[] { 0xB3, 0xFC, 0x08, 0xCC }, // 2: South-east
    new byte[] { 0x53, 0x89, 0xB0, 0xD2 }, // 3: North-east
    new byte[] { 0xD3, 0x81, 0x9C, 0x6C }, // 4: North
    new byte[] { 0xD3, 0xE1, 0x52, 0xF2 }, // 5: South
    new byte[] { 0xC3, 0x04, 0xC0, 0x8B }, // 6: East
    new byte[] { 0x43, 0xB7, 0x84, 0x8F }  // 7: West
};

        private static readonly (int offsetX, int offsetY)[,] buoy1_docking_positions = new (int, int)[8, 2]
        {
        { (-3, -3), (-3, -4) }, // 0: harbor_sw
        { (-3,  3), (-3,  4) }, // 1: harbor_nw
        { ( 3, -3), ( 3, -4) }, // 2: harbor_se
        { ( 3,  3), ( 3,  4) }, // 3: harbor_ne
        { ( -1,  3), ( 1,  3) }, // 4: harbor_n
        { ( -1, -3), ( 1, -3) }, // 5: harbor_s
        { ( 3,  -1), ( 3,  1) }, // 6: harbor_e
        { (-3,  -1), (-3,  1) }  // 7: harbor_w
        };

        private static readonly (int offsetX, int offsetY)[,] buoy2_docking_positions = new (int, int)[8, 2]
        {
        { (-2, -4), (-4, -5) }, // 0: harbor_sw
        { (-2,  4), (-4,  5) }, // 1: harbor_nw
        { ( 2, -4), ( 4, -5) }, // 2: harbor_se
        { ( 2,  4), ( 4,  5) }, // 3: harbor_ne
        { ( -1,  5), ( 1,  5) }, // 4: harbor_n
        { ( -1, -5), ( 1, -5) }, // 5: harbor_s
        { ( 5,  -1), ( 5,  1) }, // 6: harbor_e
        { (-5,  -1), (-5,  1) }  // 7: harbor_w
        };

        private static readonly byte[][] CaveTypes = new byte[][]
    {
        new byte[] { 0xD8, 0x70, 0xB3, 0xA3 }, // 0: AnimalSpawn (Deer, Elk, Rabbit)
        new byte[] { 0x23, 0x89, 0xA5, 0x07 }, // 1: SheepSpawn
        new byte[] { 0x33, 0x10, 0x75, 0xBE }, // 2: DeerSpawn
        new byte[] { 0x53, 0xB9, 0x3D, 0x52 }, // 3: RabbitSpawn
        new byte[] { 0x72, 0xC8, 0xA5, 0xFC }, // 4: __Highland Bear Spawn
        new byte[] { 0x73, 0xC8, 0xA5, 0xFC }, // 5: !!!MED Bear Spawn
        new byte[] { 0x74, 0xC8, 0xA5, 0xFC }, // 6: Bear Spawn
        new byte[] { 0x75, 0xC8, 0xA5, 0xFC }, // 7: --Snow Polar Bear Spawn (+ Mountain Hare)
        new byte[] { 0x76, 0xC8, 0xA5, 0xFC }, // 8: __Highland Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Highland Cattle)
        new byte[] { 0x77, 0xC8, 0xA5, 0xFC }, // 9: Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Ox)
        new byte[] { 0x78, 0xC8, 0xA5, 0xFC }, // 10: !!!MED Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Ox)
        new byte[] { 0x79, 0xC8, 0xA5, 0xFC }  // 11: !!!MED Camel Spawn
    };

        private static readonly byte[][] DnG_textures = new byte[][]
    {
        new byte[] { 0x89, 0xA5, 0x1C, 0xFA }, // [0]  !!!MED (RES) rocky earth
        new byte[] { 0x86, 0xA5, 0x1C, 0xFA }, // [1]  !!!MED (RES) rocky earth big
        new byte[] { 0x88, 0xA5, 0x1C, 0xFA }, // [2]  !!!MED (RES) rocky earth dark
        new byte[] { 0x87, 0xA5, 0x1C, 0xFA }, // [3]  !!!MED (RES) rocky plants
        new byte[] { 0x70, 0xA5, 0x1C, 0xFA }, // [4]  !!!MED ground 00
        new byte[] { 0x71, 0xA5, 0x1C, 0xFA }, // [5]  !!!MED ground 01
        new byte[] { 0x60, 0xA5, 0x1C, 0xFA }, // [6]  !!!MED meadow 00
        new byte[] { 0x61, 0xA5, 0x1C, 0xFA }, // [7]  !!!MED meadow 01
        new byte[] { 0x62, 0xA5, 0x1C, 0xFA }, // [8]  !!!MED meadow 02
        new byte[] { 0x63, 0xA5, 0x1C, 0xFA }, // [9]  !!!MED meadow 03
        new byte[] { 0x80, 0xA5, 0x1C, 0xFA }, // [10] !!!MED rock
        new byte[] { 0x81, 0xA5, 0x1C, 0xFA }, // [11] !!!MED rock big
        new byte[] { 0x83, 0xA5, 0x1C, 0xFA }, // [12] !!!MED rock red
        new byte[] { 0x85, 0xA5, 0x1C, 0xFA }, // [13] !!!MED rock red big
        new byte[] { 0x84, 0xA5, 0x1C, 0xFA }, // [14] !!!MED rock red small
        new byte[] { 0x82, 0xA5, 0x1C, 0xFA }, // [15] !!!MED rock small
        new byte[] { 0x90, 0xA5, 0x1C, 0xFA }, // [16] !!!MED seaground rock
        new byte[] { 0x91, 0xA5, 0x1C, 0xFA }, // [17] !!!MED seaground rock red
        new byte[] { 0x8A, 0xA5, 0x1C, 0xFA }, // [18] !!!MED stone ground
        new byte[] { 0x03, 0xDE, 0xCA, 0xDE }, // [19] ((00 LAVA 01
        new byte[] { 0x0A, 0xDE, 0xCA, 0xDE }, // [20] ((00 LAVA 01 soft
        new byte[] { 0x08, 0xDE, 0xCA, 0xDE }, // [21] ((00 LAVA 02
        new byte[] { 0x70, 0xDB, 0x7A, 0xF6 }, // [22] ((00 LAVA Meadow 00
        new byte[] { 0x70, 0xBB, 0xCA, 0xF1 }, // [23] ((00 LAVA Sand 00
        new byte[] { 0x02, 0xDE, 0xCA, 0xDE }, // [24] ((00 LAVA ground
        new byte[] { 0x09, 0xDE, 0xCA, 0xDE }, // [25] ((00 LAVA ground flat
        new byte[] { 0x07, 0xDE, 0xCA, 0xDE }, // [26] ((00 LAVA ground rough
        new byte[] { 0x04, 0xDE, 0xCA, 0xDE }, // [27] ((00 LAVA rock
        new byte[] { 0x05, 0xDE, 0xCA, 0xDE }, // [28] ((00 LAVA rock big
        new byte[] { 0xB0, 0xFA, 0x87, 0xCA }, // [29] ((00 LAVA rock floating lava
        new byte[] { 0x06, 0xDE, 0xCA, 0xDE }, // [30] ((00 LAVA rock small
        new byte[] { 0xFF, 0xCA, 0xFE, 0xCA }, // [31] (RES) rocky earth
        new byte[] { 0x02, 0xCB, 0xFE, 0xCA }, // [32] (RES) rocky earth big
        new byte[] { 0x04, 0xCB, 0xFE, 0xCA }, // [33] (RES) rocky earth dark
        new byte[] { 0x03, 0xCB, 0xFE, 0xCA }, // [34] (RES) rocky plants
        new byte[] { 0x1A, 0x70, 0x56, 0xCA }, // [35] DO NOT USE
        new byte[] { 0x01, 0xDE, 0xCA, 0xDE }, // [36] HARBOR
        new byte[] { 0x73, 0x18, 0xD3, 0x76 }, // [37] border
        new byte[] { 0xC2, 0xFA, 0x45, 0x45 }, // [38] earth
        new byte[] { 0xC4, 0xFA, 0x45, 0x45 }, // [39] leaf
        new byte[] { 0xE3, 0xE8, 0xE4, 0xBF }, // [40] meadow
        new byte[] { 0xC3, 0xFA, 0x45, 0x45 }, // [41] meadow bright
        new byte[] { 0xC6, 0xFA, 0x45, 0x45 }, // [42] meadow dark small
        new byte[] { 0x10, 0x11, 0x5E, 0xDE }, // [43] meadow ground
        new byte[] { 0xC5, 0xFA, 0x45, 0x45 }, // [44] meadow leaf
        new byte[] { 0xC7, 0xFA, 0x45, 0x45 }, // [45] meadow red flowers
        new byte[] { 0xC1, 0xFA, 0x45, 0x45 }, // [46] meadow yellow flowers
        new byte[] { 0xFE, 0xAF, 0x0F, 0xD0 }, // [47] rock
        new byte[] { 0xEF, 0xBE, 0xAD, 0xDE }, // [48] rock big
        new byte[] { 0xFE, 0xCA, 0xFE, 0xCA }, // [49] rock small
        new byte[] { 0x00, 0xCB, 0xFE, 0xCA }, // [50] rock stretched x
        new byte[] { 0x01, 0xCB, 0xFE, 0xCA }, // [51] rock stretched y
        new byte[] { 0x0D, 0xB0, 0xDE, 0xBA }, // [52] sand
        new byte[] { 0x0E, 0xB0, 0xDE, 0xBA }, // [53] sand stones
        new byte[] { 0x0B, 0xB0, 0xBE, 0xBA }, // [54] seaground
        new byte[] { 0xE4, 0x74, 0x33, 0x01 }, // [55] seaground plants
        new byte[] { 0xE6, 0x74, 0x33, 0x01 }, // [56] seaground plants rock
        new byte[] { 0xE7, 0x74, 0x33, 0x01 }, // [57] seaground rock
        new byte[] { 0xE8, 0x74, 0x33, 0x01 }, // [58] seaground rocky
        new byte[] { 0xE5, 0x74, 0x33, 0x01 }, // [59] seaground sand
        new byte[] { 0xFF, 0xE0, 0xAD, 0x0F }, // [60] snow
        new byte[] { 0x05, 0xCB, 0xFE, 0xCA }, // [61] stone ground
        new byte[] { 0xE4, 0x04, 0x00, 0x68 }, // [62] swamp land
        new byte[] { 0xE6, 0x04, 0x00, 0x68 }, // [63] swamp meadow (unblocked)
        new byte[] { 0xE5, 0x04, 0x00, 0x68 }, // [64] swamp water
        new byte[] { 0xB3, 0xD1, 0x6B, 0xFE }, // [65] water
        new byte[] { 0xC0, 0xA8, 0x7F, 0x77 }, // [66] §§Desert earth
        new byte[] { 0xC9, 0xFA, 0x45, 0x45 }, // [67] §§Desert meadow
        new byte[] { 0x0F, 0xB0, 0xDE, 0xBA }, // [68] §§Desert sand dune
        new byte[] { 0x12, 0xB0, 0xDE, 0xBA }, // [69] §§Desert sand ripple
        new byte[] { 0x11, 0xB0, 0xDE, 0xBA }, // [70] §§Desert sand small dune
        new byte[] { 0x13, 0xB0, 0xDE, 0xBA }, // [71] §§Desert sand small ripple
        new byte[] { 0x10, 0xB0, 0xDE, 0xBA }  // [72] §§Desert sand yellow
    };

        // Array storing the texture type corresponding to each terrain index (0 to 72)
        private static readonly int[] DnG_texture_types = new int[]
        {
        2, // [0]  !!!MED (RES) rocky earth 2
        2, // [1]  !!!MED (RES) rocky earth big 2
        2, // [2]  !!!MED (RES) rocky earth dark 2
        2, // [3]  !!!MED (RES) rocky plants 2
        1, // [4]  !!!MED ground 00 1
        1, // [5]  !!!MED ground 01 1
        1, // [6]  !!!MED meadow 00 1
        1, // [7]  !!!MED meadow 01 1
        1, // [8]  !!!MED meadow 02 1
        1, // [9]  !!!MED meadow 03 1
        2, // [10] !!!MED rock 2
        2, // [11] !!!MED rock big 2
        2, // [12] !!!MED rock red 2
        2, // [13] !!!MED rock red big 2
        2, // [14] !!!MED rock red small 2
        2, // [15] !!!MED rock small 2
        3, // [16] !!!MED seaground rock 3
        3, // [17] !!!MED seaground rock red 3
        1, // [18] !!!MED stone ground 1
        4, // [19] ((00 LAVA 01 4
        4, // [20] ((00 LAVA 01 soft 4
        4, // [21] ((00 LAVA 02 4
        1, // [22] ((00 LAVA Meadow 00 1
        3, // [23] ((00 LAVA Sand 00 3
        1, // [24] ((00 LAVA ground 1
        1, // [25] ((00 LAVA ground flat 1
        1, // [26] ((00 LAVA ground rough 1
        2, // [27] ((00 LAVA rock 2
        2, // [28] ((00 LAVA rock big 2
        2, // [29] ((00 LAVA rock floating lava 2
        2, // [30] ((00 LAVA rock small 2
        2, // [31] (RES) rocky earth 2
        2, // [32] (RES) rocky earth big 2
        2, // [33] (RES) rocky earth dark 2
        2, // [34] (RES) rocky plants 2
        1, // [35] DO NOT USE 1
        1, // [36] HARBOR 1
        4, // [37] border 4
        1, // [38] earth 1
        1, // [39] leaf 1
        1, // [40] meadow 1
        1, // [41] meadow bright 1
        1, // [42] meadow dark small 1
        1, // [43] meadow ground 1
        1, // [44] meadow leaf 1
        1, // [45] meadow red flowers 1
        1, // [46] meadow yellow flowers 1
        2, // [47] rock 2
        2, // [48] rock big 2
        2, // [49] rock small 2
        2, // [50] rock stretched x 2
        2, // [51] rock stretched y 2
        3, // [52] sand 3
        1, // [53] sand stones 1
        3, // [54] seaground 3
        3, // [55] seaground plants 3
        3, // [56] seaground plants rock 3
        3, // [57] seaground rock 3
        3, // [58] seaground rocky 3
        3, // [59] seaground sand 3
        4, // [60] snow 4
        1, // [61] stone ground 1
        4, // [62] swamp land 4
        1, // [63] swamp meadow (unblocked) 1
        4, // [64] swamp water 4
        4, // [65] water 4
        1, // [66] §§Desert earth 1
        1, // [67] §§Desert meadow 1
        3, // [68] §§Desert sand dune 3
        3, // [69] §§Desert sand ripple 3
        3, // [70] §§Desert sand small dune 3
        3, // [71] §§Desert sand small ripple 3
        3  // [72] §§Desert sand yellow 3
        };

        // Array storing the 4-byte sequences for each terrain entry (index 0 to 40)
        private static readonly byte[][] AdK_textures = new byte[][]
        {
        new byte[] { 0x02, 0x4A, 0xC4, 0x7A }, // [0]  __Highland meadow bright
        new byte[] { 0x03, 0x4A, 0xC4, 0x7A }, // [1]  __Highland meadow bright rocks
        new byte[] { 0x04, 0x4A, 0xC4, 0x7A }, // [2]  __Highland meadow medium
        new byte[] { 0x05, 0x4A, 0xC4, 0x7A }, // [3]  __Highland meadow medium rocks
        new byte[] { 0x06, 0x4A, 0xC4, 0x7A }, // [4]  __Highland meadow dark
        new byte[] { 0x07, 0x4A, 0xC4, 0x7A }, // [5]  __Highland meadow dark rocks
        new byte[] { 0x00, 0x4D, 0xC4, 0x7A }, // [6]  __Highland earth fir moss
        new byte[] { 0x01, 0x4D, 0xC4, 0x7A }, // [7]  __Highland earth fir
        new byte[] { 0x02, 0x4D, 0xC4, 0x7A }, // [8]  __Highland earth
        new byte[] { 0x02, 0x4B, 0xC4, 0x7A }, // [9]  __Highland rock
        new byte[] { 0x03, 0x4B, 0xC4, 0x7A }, // [10] __Highland rock big
        new byte[] { 0x04, 0x4B, 0xC4, 0x7A }, // [11] __Highland (RES) rocky earth
        new byte[] { 0x05, 0x4B, 0xC4, 0x7A }, // [12] __Highland rock flat
        new byte[] { 0x06, 0x4B, 0xC4, 0x7A }, // [13] __Highland rock dark big
        new byte[] { 0x07, 0x4B, 0xC4, 0x7A }, // [14] __Highland rock dark flat
        new byte[] { 0x08, 0x4B, 0xC4, 0x7A }, // [15] __Highland rock braid flat
        new byte[] { 0x0D, 0x4B, 0xC4, 0x7A }, // [16] __Highland stone ground
        new byte[] { 0x09, 0x4B, 0xC4, 0x7A }, // [17] --Snow highland rock much
        new byte[] { 0x0A, 0x4B, 0xC4, 0x7A }, // [18] --Snow highland rock
        new byte[] { 0x0B, 0x4B, 0xC4, 0x7A }, // [19] --Snow highland rock part
        new byte[] { 0x0C, 0x4B, 0xC4, 0x7A }, // [20] --Snow (RES) rocky earth
        new byte[] { 0x0B, 0x4E, 0xC4, 0x7A }, // [21] --Snow meadow
        new byte[] { 0x0C, 0x4E, 0xC4, 0x7A }, // [22] --Snow meadow snow
        new byte[] { 0x0D, 0x4E, 0xC4, 0x7A }, // [23] --Snow meadow snow 2
        new byte[] { 0x0E, 0x4E, 0xC4, 0x7A }, // [24] --Snow meadow snow 3
        new byte[] { 0x0F, 0x4E, 0xC4, 0x7A }, // [25] --Snow meadow Treeground 80x80,200x200
        new byte[] { 0x10, 0x4E, 0xC4, 0x7A }, // [26] --Snow meadow Treeground 125x125
        new byte[] { 0x11, 0x4E, 0xC4, 0x7A }, // [27] --Snow meadow Treeground 170x170
        new byte[] { 0x12, 0x4E, 0xC4, 0x7A }, // [28] --Snow meadow Treeground 255x255
        new byte[] { 0x10, 0x4C, 0xC4, 0x7A }, // [29] __Highland swamp land
        new byte[] { 0x11, 0x4C, 0xC4, 0x7A }, // [30] __Highland swamp water
        new byte[] { 0x12, 0x4C, 0xC4, 0x7A }, // [31] __Highland swamp meadow (unblocked)
        new byte[] { 0x02, 0x4C, 0xC4, 0x7A }, // [32] __Highland seaground rocks
        new byte[] { 0x03, 0x4C, 0xC4, 0x7A }, // [33] __Highland seaground rocks dark flat
        new byte[] { 0x04, 0x4C, 0xC4, 0x7A }, // [34] __Highland seaground pebbles
        new byte[] { 0x0E, 0x5E, 0xC4, 0x7A }, // [35] --Snow Ice Crackles
        new byte[] { 0x0F, 0x5E, 0xC4, 0x7A }, // [36] --Snow Ice Crackles Dark
        new byte[] { 0x10, 0x5E, 0xC4, 0x7A }, // [37] --Snow Ice Clean
        new byte[] { 0x13, 0x5E, 0xC4, 0x7A }, // [38] --Snow Ice Clean Dark
        new byte[] { 0x11, 0x5E, 0xC4, 0x7A }, // [39] --Snow medium border
        new byte[] { 0x12, 0x5E, 0xC4, 0x7A }  // [40] --Snow soft border
        };

        // Array storing the texture type corresponding to each terrain index (0 to 40)
        private static readonly int[] AdK_texture_types = new int[]
        {
        1, // [0]  __Highland meadow bright 1
        1, // [1]  __Highland meadow bright rocks 1
        1, // [2]  __Highland meadow medium 1
        1, // [3]  __Highland meadow medium rocks 1
        1, // [4]  __Highland meadow dark 1
        1, // [5]  __Highland meadow dark rocks 1
        1, // [6]  __Highland earth fir moss 1
        1, // [7]  __Highland earth fir 1
        1, // [8]  __Highland earth 1
        2, // [9]  __Highland rock 2
        2, // [10] __Highland rock big 2
        2, // [11] __Highland (RES) rocky earth 2
        2, // [12] __Highland rock flat 2
        2, // [13] __Highland rock dark big 2
        2, // [14] __Highland rock dark flat 2
        2, // [15] __Highland rock braid flat 2
        1, // [16] __Highland stone ground 1
        2, // [17] --Snow highland rock much 2
        2, // [18] --Snow highland rock 2
        2, // [19] --Snow highland rock part 2
        2, // [20] --Snow (RES) rocky earth 2
        1, // [21] --Snow meadow 1
        1, // [22] --Snow meadow snow 1
        1, // [23] --Snow meadow snow 2 1
        1, // [24] --Snow meadow snow 3 1
        1, // [25] --Snow meadow Treeground 80x80,200x200 1
        1, // [26] --Snow meadow Treeground 125x125 1
        1, // [27] --Snow meadow Treeground 170x170 1
        1, // [28] --Snow meadow Treeground 255x255 1
        4, // [29] __Highland swamp land 4
        4, // [30] __Highland swamp water 4
        1, // [31] __Highland swamp meadow (unblocked) 1
        3, // [32] __Highland seaground rocks 3
        3, // [33] __Highland seaground rocks dark flat 3
        3, // [34] __Highland seaground pebbles 3
        4, // [35] --Snow Ice Crackles 4
        4, // [36] --Snow Ice Crackles Dark 4
        4, // [37] --Snow Ice Clean 4
        4, // [38] --Snow Ice Clean Dark 4
        4, // [39] --Snow medium border 4
        4  // [40] --Snow soft border 4
        };
    }
}