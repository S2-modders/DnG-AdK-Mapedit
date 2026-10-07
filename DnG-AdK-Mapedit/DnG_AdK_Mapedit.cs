using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using File = System.IO.File;

namespace DnG_AdK_Mapedit
{
    public partial class DnG_AdK_Mapedit : Form
    {
        private void Changelog_button_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Changelog_button.LinkVisited = true;
            string message = @"Changes compared to the original map converter:

• (Beta 3) Dark mode support was added
• Invalid resources are now automatically removed
• Swapping was added to allow using new assets
• Harbour code was added but currently it's causing game crashes
• Caves section now works properly
• Knowledge of exact sacrifice names is not required as icons are displayed instead
• Sacrifice limits are now automatically checked and displayed
• Each sacrifice preset is now stored in individual files and can be easily exported
• Default player colours and (Beta 4) difficulties can now be customized
• (Beta 3) Added ability to create custom environment files
• Whole map preset can be now saved not requiring inputting values manually with each map edit
• (Beta 4) Map creator receives an information about forester crash fix
• Support for maps with odd player counts was added
• Maps no longer crash randomly during gameplay
• Resource signs placed by map creators now never despawn";

            MessageBox.Show(message, "Changelog", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        readonly string TempFolder = Path.Combine(Path.GetTempPath(), "DnG-AdK-Mapedit/");
        private string WorkingFileName => Path.Combine(TempFolder, "temp_" + Path.GetFileName(DnG_map_path.Text));
        private string ArchiverPath => Path.Combine(TempFolder, "decryptor_s2.exe");

        // Keep a reference to the external process so it is not GC-collected
        private Process archiverProcess;
        //Archiver data
        private bool DnG;
        private bool Compress;
        private string sourceFileName;
        private string destinationFileName;

        private int Player_count;

        private int map_size_x;
        private int map_size_y;

        private static readonly byte[] HeightsHeader = [0x01, 0x00, 0x00, 0x00, 0x71, 0x28, 0x0B, 0x82, 0x0C, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x9C, 0xFF, 0xFF, 0xFF];
        private static readonly byte[] TexturesHeader = [0x00, 0x00, 0x00, 0x00, 0xB4, 0x88, 0xC8, 0x75, 0x0A, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
        private static readonly byte[] Resources_header = [0x00, 0x00, 0x00, 0x00, 0xB0, 0xBB, 0xC3, 0x7C, 0x0D, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];

        private const uint CoalHex = 0x7068DCD3;      // Little-endian values
        private const uint IronHex = 0xEC5020BE;
        private const uint SaltHex = 0x09D2D623;
        private const uint GoldHex = 0x4F41C633;
        private const uint GemstonesHex = 0xCB98C903;
        private const uint StoneHex = 0x55E952D3;
        private const uint FishHex = 0x4012E5A3;
        private const uint WaterHex = 0xA9676263;

        private const uint EmptyHex = 0xFFFFFFFF;

        private readonly List<(int tab, int from, int to)> Swap_list = [];

        private readonly List<(int pos_x, int pos_y, int rotation, bool anchorage, int anchor_x, int anchor_y, int buoy_1_connection, int buoy_2_connection)> Harbours_list = [];
        // Flag to prevent UI updates from triggering save events
        private bool isUpdatingUI = false;

        private readonly List<(int pos_x, int pos_y, int type)> Caves_list = [];

        int current_zone_index = -1;
        private readonly List<(Color fog_colour, Color ambient_colour, Color light_colour, float shadow_intensity, int fog_start_distance, int fog_full_distance, int pos_x, int pos_y, int radius, int transition)> Environment_zones = [];

        public DnG_AdK_Mapedit()
        {
            //Uncomment to test multi-language support
            //Thread.CurrentThread.CurrentUICulture = new CultureInfo("pl-PL");
            //Thread.CurrentThread.CurrentCulture = new CultureInfo("pl-PL");

            Directory.CreateDirectory(TempFolder);

            InitializeComponent();
        }

        private void DnG_AdK_mapedit_Load(object sender, System.EventArgs e)
        {
            //Apply dark mode
            if (Application.IsDarkModeEnabled)
            {
                Changelog_button.LinkColor = Color.SkyBlue;
                Map_info_name.LinkColor = Color.SkyBlue;
                Map_info_resources_share.LinkColor = Color.SkyBlue;
                Export_forester_fix.LinkColor = Color.SkyBlue;
            }

            Resources_wait.Visible = false;
            Export_wait.Visible = false;
            Tab_control.Enabled = false;

            Resources_swap_button.Enabled = false;
            Textures_swap_button.Enabled = false;
            Logical_grid_swap_button.Enabled = false;
            Small_doodads_swap_button.Enabled = false;

            Swap_move_down_button.Enabled = false;
            Swap_remove_button.Enabled = false;
            Swap_move_up_button.Enabled = false;

            //For now disable broken harbour section
            Harbours_tab.Enabled = false;

            Harbours_remove_button.Enabled = false;
            Harbour_panel.Enabled = false;
            Harbour_anchor_panel.Enabled = false;

            Caves_remove_button.Enabled = false;
            Cave_panel.Enabled = false;

            Environment_preset_select.Enabled = false;
            Environment_preset_global.Enabled = false;
            Environment_preset_local.Enabled = false;
            Environment_panel.Enabled = false;
            Environment_zone_panel.Enabled = false;

            Environment_remove_zone.Enabled = false;
            Environment_previous_zone.Enabled = false;
            Environment_next_zone.Enabled = false;

            //Set default values
            Global_sky_select.SelectedIndex = 1;
            Global_sun_placement_input.Value = 55;

            Global_sun_height_input.Value = 50;
            Global_shadow_intensity_input.Value = 90;

            Global_fog_start_input.Value = 200;
            Global_fog_full_input.Value = 300;

            Global_fog_colour.BackColor = ColorTranslator.FromHtml("#CCE6FF");
            Global_light_colour.BackColor = ColorTranslator.FromHtml("#998099");
            Global_ambient_colour.BackColor = ColorTranslator.FromHtml("#EFE5CF");

            Sacrifices_included_presets_select.SelectedIndex = 0;
            Sacrifices_included_presets_select.Enabled = false;
        }

        //User interacts with the DnG map file path textbox
        private void DnG_map_path_MouseDown(object sender, MouseEventArgs e)
        {
            using OpenFileDialog openFileDialog = new();
            openFileDialog.Filter = "DnG map file (*.s2m)|*.s2m|All files (*.*)|*.*";
            openFileDialog.Title = "Select a DnG map file";
            if (openFileDialog.ShowDialog() == DialogResult.OK)
            {
                DnG_map_path.Text = openFileDialog.FileName;
                FileValidation();
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
            byte[] File_header = [.. DnG_map.Take(8)];
            //Only compressed
            byte[] DnG_header = [0x12, 0x18, 0x09, 0x06, 0x72, 0x63, 0x30, 0x30];

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
            byte[] SAdK_header = [0x12, 0x18, 0x09, 0x06, 0x73, 0x61, 0x64, 0x6B];

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
            if (!File.Exists(ArchiverPath))
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using Stream stream = assembly.GetManifestResourceStream("DnG_AdK_Mapedit.decryptor_s2.exe");
                using FileStream fileStream = new(ArchiverPath, FileMode.Create, FileAccess.Write);
                stream.CopyTo(fileStream);
            }

            // Many programs that accept files dragged onto their icon simply receive the
            // dropped file path as the first command-line argument. Replicate that by
            // starting the decryptor with the map filename as an argument. Quote the
            // argument to handle spaces in paths and set the working directory so the
            // decryptor sees the copied file in its current folder.
            // Store the process in a field so the GC won't collect it before it exits
            archiverProcess = new Process();
            archiverProcess.StartInfo.FileName = ArchiverPath;

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
            if (File.Exists(ArchiverPath))
            {
                File.Delete(ArchiverPath);
            }
        }

        //Archiver process exit
        private void ExternalApp_Exited(object sender, EventArgs e)
        {
            this.Invoke((System.Windows.Forms.MethodInvoker)delegate
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
                            if (Export_preview_copy.Checked)
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

                            //Moving the fog file
                            if (Environment_preset_checkbox.Checked)
                            {
                                string fog_file_destination = Path.Combine(Path.GetDirectoryName(destinationFileName), Map_info_name.Text + ".bin");
                                if (File.Exists(fog_file_destination))
                                {
                                    File.Delete(fog_file_destination);
                                }
                                File.Move(Path.ChangeExtension(sourceFileName, ".bin"), fog_file_destination);
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
                using var stream = new FileStream(bmpPath, FileMode.Open, FileAccess.Read);
                Map_info_preview.BackgroundImage = Image.FromStream(stream);
            }
            else
            {
                Export_preview_copy.Enabled = false;
            }

            byte[] DnG_map = File.ReadAllBytes(WorkingFileName);
            int current_byte = 0;
            //Skip the header
            current_byte += 12;
            //Read player count
            Player_count = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            Map_info_player_amount.Text = "Player count: " + Player_count.ToString();
            if (Player_count < 2)
            {
                Export_multiplayer_prefix.Enabled = false;
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

            //Group controls into an array for easy iteration
            var colour_selectors = new[]
            {
                Players_1_colour_select,
                Players_2_colour_select,
                Players_3_colour_select,
                Players_4_colour_select,
                Players_5_colour_select,
                Players_6_colour_select
            };

            //00 00 00 00 -> weak
            //01 00 00 00 -> normal
            //02 00 00 00 -> strong

            var difficulty_selectors = new[]
            {
                Players_1_difficulty_select,
                Players_2_difficulty_select,
                Players_3_difficulty_select,
                Players_4_difficulty_select,
                Players_5_difficulty_select,
                Players_6_difficulty_select
            };

            //Default colour presets for each player count
            int[][] presets =
            [
                [0],                  // 1 player
                [0, 1],               // 2 players
                [0, 2, 3],            // 3 players
                [0, 2, 3, 1],         // 4 players
                [0, 2, 3, 1, 6],      // 5 players
                [0, 2, 3, 5, 1, 4]    // 6 players
            ];

            //Applying default player information
            if (Player_count >= 1 && Player_count <= colour_selectors.Length)
            {
                int[] activePreset = presets[Player_count - 1];

                for (int i = 0; i < colour_selectors.Length; i++)
                {
                    bool isActive = i < Player_count;
                    colour_selectors[i].Enabled = isActive;
                    difficulty_selectors[i].Enabled = isActive;

                    if (isActive)
                    {
                        colour_selectors[i].SelectedIndex = activePreset[i];
                        difficulty_selectors[i].SelectedIndex = 2; // Default to "strong" difficulty
                    }
                }
            }

            //Skip start positions
            current_byte += 20 * Player_count;
            //Read map name length
            int map_name_length = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            current_byte += 4;

            //Decode using Windows-1252 encoding
            Encoding win1252 = Encoding.GetEncoding(1252);
            string map_name = win1252.GetString(DnG_map, current_byte, map_name_length);

            Map_info_name.Text = map_name;
            current_byte += map_name_length;
            //Read map size
            map_size_x = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            current_byte += 4;
            map_size_y = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            current_byte += 4;
            Map_info_size.Text = "Map size: " + map_size_x.ToString() + "x" + map_size_y.ToString();

            //Update maximum positions
            Harbour_X_input.Maximum = map_size_x - 1;
            Harbour_Y_input.Maximum = map_size_y - 1;
            Anchor_X_input.Maximum = map_size_x - 1;
            Anchor_Y_input.Maximum = map_size_y - 1;

            Cave_X_input.Maximum = map_size_x - 1;
            Cave_Y_input.Maximum = map_size_y - 1;

            int max_radius = (int)Math.Ceiling(Math.Sqrt(map_size_x * map_size_x + map_size_y * map_size_y));

            Local_X_input.Minimum = -max_radius;
            Local_X_input.Maximum = map_size_x - 1 + max_radius;
            Local_Y_input.Minimum = -max_radius;
            Local_Y_input.Maximum = map_size_y - 1 + max_radius;
            Local_radius_input.Maximum = max_radius;
            Local_transition_input.Maximum = max_radius;

            UpdateResources(current_byte, DnG_map);
        }

        void UpdateResources(int current_byte, byte[] DnG_map)
        {

            byte[] empty_hex_extended = [0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF];

            //Finding the heights array header in the map file
            current_byte = FindSequenceOffset(DnG_map, HeightsHeader, current_byte);

            if (current_byte == -1)
            {
                MessageBox.Show("Heights array not found in the map file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            //Skip map size
            //int heights_beginning = current_byte + 8;

            //Finding the textures array header in the map file
            current_byte = FindSequenceOffset(DnG_map, TexturesHeader, current_byte);


            if (current_byte == -1)
            {
                MessageBox.Show("Textures array not found in the map file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            //Skip array length (map_size_x * map_size_y)
            int textures_beginning = current_byte + 4;

            //Finding the resource array header in the map file
            current_byte = FindSequenceOffset(DnG_map, Resources_header, current_byte);

            if (current_byte == -1)
            {
                MessageBox.Show("Resource array not found in the map file.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            //Skip map size
            current_byte += 8;
            int Resource_array_length = map_size_x * map_size_y;

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
                if (BitConverter.ToInt32(DnG_map, current_byte) > 0)
                {
                    current_byte += 4; // Skip the resource amount

                    // Skip empty resources
                    if (EmptyHex == BitConverter.ToUInt32(DnG_map, current_byte))
                    {
                        current_byte += 4;
                    }
                    /*
                    //Map editor fails to remove invalid resources that are under the water.
                    //That causes the resource count to be different.
                    //Remove invalid resources that are under the water
                    else if (!IsOnLand(DnG_map, heights_beginning, j, map_size_x))
                    {
                        // Overwrite the resource amount (4 bytes) and type (4 bytes) for the current entry.
                        Array.Copy(Empty_hex_extended, 0, DnG_map, current_byte - 4, Empty_hex_extended.Length);
                        current_byte += 4; // move past the type to the next resource's amount
                    }
                    */
                    //Remove invalid resources that are not on rock textures
                    else if (!IsRockTexture(DnG_map, j, textures_beginning))
                    {
                        int resourceType = BitConverter.ToInt32(DnG_map, current_byte);
                        if (resourceType != FishHex)
                        {
                            //Clear both amount and type for this resource entry.
                            Array.Copy(empty_hex_extended, 0, DnG_map, current_byte - 4, empty_hex_extended.Length);
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
                                Array.Copy(empty_hex_extended, 0, DnG_map, current_byte - 4, 8);
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

            SetResourceUI(Map_info_coal_amount, Map_info_coal_share, Coal_count, Total_resources);
            SetResourceUI(Map_info_iron_amount, Map_info_iron_share, Iron_count, Total_resources);
            SetResourceUI(Map_info_salt_amount, Map_info_salt_share, Salt_count, Total_resources);
            SetResourceUI(Map_info_gold_amount, Map_info_gold_share, Gold_count, Total_resources);
            SetResourceUI(Map_info_gemstones_amount, Map_info_gemstones_share, Gemstones_count, Total_resources);
            SetResourceUI(Map_info_stone_amount, Map_info_stone_share, Stone_count, Total_resources);

            //Write the edited file back to disk
            File.WriteAllBytes(WorkingFileName, DnG_map);

            Resources_wait.Visible = false;
            Tab_control.Enabled = true;
        }

        private static void SetResourceUI(Label amountControl, Label shareControl, int count, int total)
        {
            amountControl.Text = count.ToString();
            shareControl.Text = total == 0
                ? "NaN%"
                : (count * 100.0 / total).ToString("F2") + "%";
        }

        private static int FindSequenceOffset(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern, int startIndex = 0)
        {
            int index = data[startIndex..].IndexOf(pattern);
            return index >= 0 ? startIndex + index + pattern.Length : -1;
        }

        //Checking if the resource is not under water
        /*
        private static bool IsOnLand(byte[] DnG_map, int Heights_array_beginning, int Index_logical, int Map_size_x)
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
        */

        public static bool IsRockTexture(byte[] DnG_map, int Index, int textures_beginning)
        {
            // Convert 4 bytes to uint
            uint value = BitConverter.ToUInt32(DnG_map, textures_beginning + Index * 4);

            // Convert to Big-Endian to match human-readable hex values
            if (BitConverter.IsLittleEndian)
            {
                value = (value >> 24) |
                       ((value >> 8) & 0x0000FF00) |
                       ((value << 8) & 0x00FF0000) |
                       (value << 24);
            }

            return value switch
            {
                0xFEAF0FD0 or 0xEFBEADDE or 0xFECAFECA or 0xFFCAFECA or 0x00CBFECA or 0x01CBFECA or 0x02CBFECA or 0x03CBFECA or 0x04CBFECA or 0x04DECADE or 0x05DECADE or 0x06DECADE or 0xB0FA87CA or 0x80A51CFA or 0x81A51CFA or 0x82A51CFA or 0x83A51CFA or 0x84A51CFA or 0x85A51CFA or 0x86A51CFA or 0x87A51CFA or 0x88A51CFA or 0x89A51CFA => true,
                _ => false,
            };
        }

        //Map renaming dialog
        private void Map_info_name_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            using var Map_rename_dialog = new Map_renaming(Map_info_name.Text);
            if (Map_rename_dialog.ShowDialog(this) == DialogResult.OK)
            {
                Map_info_name.Text = Map_rename_dialog.Map_name;
                UpdateMapName();
            }
        }

        // Update the map name in the DnG map file
        void UpdateMapName()
        {
            byte[] DnG_map = File.ReadAllBytes(WorkingFileName);
            int current_byte = 0;

            // Skip the header
            current_byte += 12;

            // Skip start positions and the player count
            current_byte += 20 * (int)BitConverter.ToUInt32(DnG_map, current_byte) + 4;

            // Read the old map name length
            int oldNameLength = (int)BitConverter.ToUInt32(DnG_map, current_byte);
            int oldSectionLength = oldNameLength + 4; // 4-byte length prefix + name bytes

            // Encode new map name
            Encoding win1252 = Encoding.GetEncoding(1252);
            byte[] nameBytes = win1252.GetBytes(Map_info_name.Text);
            byte[] lengthBytes = BitConverter.GetBytes(nameBytes.Length);

            // Replace section in-place using slice ranges and collection expressions
            DnG_map = [
                .. DnG_map[..current_byte],
        .. lengthBytes,
        .. nameBytes,
        .. DnG_map[(current_byte + oldSectionLength)..]
            ];

            // Write the edited file back to disk
            File.WriteAllBytes(WorkingFileName, DnG_map);
        }

        //Resource share recommendations
        private void Map_info_resources_share_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Map_info_resources_share.LinkVisited = true;
            MessageBox.Show("Recommended resource shares:\n\nCoal: ~40%\nIron: ~20%\nSalt: ~20%\nGold: ~20%\nGemstones: ~3%\nStone: ~3%", "Recommended resource shares", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private void Resources_selection(object sender, EventArgs e)
        {
            if (Resources_from_list.SelectedIndex != -1 && Resources_to_list.SelectedIndex != -1)
            {
                Resources_swap_button.Enabled = true;
            }
            else
            {
                Resources_swap_button.Enabled = false;
            }
        }

        private void Resources_swap_button_Click(object sender, EventArgs e)
        {
            Resources_wait.Visible = true;
            Tab_control.Enabled = false;
            Resources_wait.Refresh();

            byte[] resources_list = [0xD3, 0xDC, 0x68, 0x70, 0xBE, 0x20, 0x50, 0xEC, 0x23, 0xD6, 0xD2, 0x09, 0x33, 0xC6, 0x41, 0x4F, 0x03, 0xC9, 0x98, 0xCB, 0xD3, 0x52, 0xE9, 0x55];

            int from_resource = BitConverter.ToInt32(resources_list, Resources_from_list.SelectedIndex * 4);
            int to_resource = BitConverter.ToInt32(resources_list, Resources_to_list.SelectedIndex * 4);
            byte[] to_resource_bytes = BitConverter.GetBytes(to_resource);

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
            int map_size_x = BitConverter.ToInt32(DnG_map, current_byte);
            current_byte += 4;
            int map_size_y = BitConverter.ToInt32(DnG_map, current_byte);
            current_byte += 4;
            int resource_array_length = map_size_x * map_size_y;
            //Skip first resource amount
            current_byte += 4;

            for (int i = 0; i < resource_array_length; i++)
            {
                if (from_resource == BitConverter.ToInt32(DnG_map, current_byte))
                {
                    Array.Copy(to_resource_bytes, 0, DnG_map, current_byte, 4);
                }
                current_byte += 8;
            }

            //Write the edited file back to disk
            File.WriteAllBytes(WorkingFileName, DnG_map);

            //Recalculating the resource shares after the resources have been swapped
            UpdateResources(0, DnG_map);
        }

        //Save the map for further editing
        private void Resources_continue_editing_button_Click(object sender, EventArgs e)
        {
            // Allow the user to keep the old map name even if it exceeds the maximum length, but warn them about it.
            if (Map_info_name.Text.Length > 20)
            {
                MessageBox.Show("Current map name with a length of " + Map_info_name.Text.Length + " characters is larger than the maximum allowed of 20 characters", "Map name is too long", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            using SaveFileDialog saveFileDialog = new();
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

        private void Textures_selection(object sender, EventArgs e)
        {
            if (Textures_from_list.SelectedIndex != -1 && Textures_to_list.SelectedIndex != -1)
            {
                Textures_swap_button.Enabled = true;
            }
            else
            {
                Textures_swap_button.Enabled = false;
            }
        }

        private void Textures_swap_button_Click(object sender, EventArgs e) =>
    AddSwapEntry(Textures_from_list, Textures_to_list, 1);

        private void Logical_grid_selection(object sender, EventArgs e)
        {
            if (Logical_grid_from_list.SelectedIndex != -1 && Logical_grid_to_list.SelectedIndex != -1)
            {
                Logical_grid_swap_button.Enabled = true;
            }
            else
            {
                Logical_grid_swap_button.Enabled = false;
            }
        }

        private void Logical_grid_swap_button_Click(object sender, EventArgs e) =>
            AddSwapEntry(Logical_grid_from_list, Logical_grid_to_list, 2);

        private void Small_doodads_selection(object sender, EventArgs e)
        {
            if (Small_doodads_from_list.SelectedIndex != -1 && Small_doodads_to_list.SelectedIndex != -1)
            {
                Small_doodads_swap_button.Enabled = true;
            }
            else
            {
                Small_doodads_swap_button.Enabled = false;
            }
        }

        private void Small_doodads_swap_button_Click(object sender, EventArgs e) =>
            AddSwapEntry(Small_doodads_from_list, Small_doodads_to_list, 3);


        // Helper method to handle all swap additions
        private void AddSwapEntry(ListBox fromList, ListBox toList, int typeId)
        {
            string displayText = $"{fromList.Text} -> {toList.Text}";

            if (!Swap_list_view.Items.Contains(displayText))
            {
                Swap_list.Add((typeId, fromList.SelectedIndex, toList.SelectedIndex));
                Swap_list_view.Items.Add(displayText);
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

        private void Swap_list_view_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (Swap_list_view.SelectedIndex > 0)
            {
                Swap_move_down_button.Enabled = true;
            }
            else
            {
                Swap_move_down_button.Enabled = false;
            }

            if (Swap_list_view.SelectedIndex != -1)
            {
                Swap_remove_button.Enabled = true;
            }
            else
            {
                Swap_remove_button.Enabled = false;
            }

            if (Swap_list_view.SelectedIndex < Swap_list_view.Items.Count - 1 && Swap_list_view.SelectedIndex != -1)
            {
                Swap_move_up_button.Enabled = true;
            }
            else
            {
                Swap_move_up_button.Enabled = false;
            }
        }

        private void Swap_move_down_button_Click(object sender, EventArgs e)
        {
            int index = Swap_list_view.SelectedIndex;

            // Ensure an item is selected and it isn't already the last item
            if (index != -1 && index < Swap_list_view.Items.Count - 1)
            {
                // 1. Swap elements in the backend data list
                (Swap_list[index + 1], Swap_list[index]) = (Swap_list[index], Swap_list[index + 1]);

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
                    // Select the item at the same stream_offset, or the last item if the end item was removed
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
                (Swap_list[index - 1], Swap_list[index]) = (Swap_list[index], Swap_list[index - 1]);

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

            Harbour_buoy_1_select.Items.Clear();
            Harbour_buoy_2_select.Items.Clear();

            Harbour_buoy_1_select.Items.Add("None");
            Harbour_buoy_2_select.Items.Add("None");

            for (int i = 0; i < Harbours_list.Count; i++)
            {
                Harbour_buoy_1_select.Items.Add($"Harbour {i + 1} buoy 1");
                Harbour_buoy_1_select.Items.Add($"Harbour {i + 1} buoy 2");
                Harbour_buoy_2_select.Items.Add($"Harbour {i + 1} buoy 1");
                Harbour_buoy_2_select.Items.Add($"Harbour {i + 1} buoy 2");
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
                var (pos_x, pos_y, rotation, anchorage, anchor_x, anchor_y, buoy_1_connection, buoy_2_connection) = Harbours_list[Harbours_list_view.SelectedIndex];

                Harbour_X_input.Value = pos_x;
                Harbour_Y_input.Value = pos_y;
                Harbour_rotation_select.SelectedIndex = rotation;

                Harbour_anchor_checkbox.Checked = anchorage;
                Harbour_anchor_panel.Enabled = anchorage;

                Anchor_X_input.Value = anchor_x;
                Anchor_Y_input.Value = anchor_y;
                Harbour_buoy_1_select.SelectedIndex = buoy_1_connection;
                Harbour_buoy_2_select.SelectedIndex = buoy_2_connection;
            }
            else
            {
                Harbour_panel.Enabled = false;

                //Load default preset
                Harbour_X_input.Value = 0;
                Harbour_Y_input.Value = 0;
                Harbour_rotation_select.SelectedIndex = -1;

                Harbour_anchor_checkbox.Checked = false;
                Harbour_anchor_panel.Enabled = false;

                Anchor_X_input.Value = 0;
                Anchor_Y_input.Value = 0;
                //"None"
                Harbour_buoy_1_select.SelectedIndex = 0;
                Harbour_buoy_2_select.SelectedIndex = 0;
            }

            isUpdatingUI = false; // Re-enable saving to list
        }

        private void Harbours_list_view_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (Harbours_list_view.SelectedIndex != -1)
            {
                Harbours_remove_button.Enabled = true;
            }
            else
            {
                Harbours_remove_button.Enabled = false;
            }

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

            int selectedB1 = Harbour_buoy_1_select.SelectedIndex;
            int selectedB2 = Harbour_buoy_2_select.SelectedIndex;
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
                Harbour_buoy_1_select.SelectedIndex = selectedB1;
                Harbour_buoy_2_select.SelectedIndex = selectedB2;
                isUpdatingUI = false;
            }

            // 6. Save data
            Harbours_list[index] = (
                (int)Harbour_X_input.Value,
                (int)Harbour_Y_input.Value,
                Harbour_rotation_select.SelectedIndex,
                Harbour_anchor_checkbox.Checked,
                (int)Anchor_X_input.Value,
                (int)Anchor_Y_input.Value,
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

            var (_, _, _, _, _, _, buoy_1_connection, buoy_2_connection) = Harbours_list[harbourIndex];
            return isBuoy1 ? buoy_1_connection : buoy_2_connection;
        }

        // Helper to overwrite a specific buoy's connection in the background
        private void SetBuoyConnection(int buoyDropdownIndex, int newTargetIndex)
        {
            if (buoyDropdownIndex == 0) return;

            int harbourIndex = (buoyDropdownIndex - 1) / 2;
            bool isBuoy1 = buoyDropdownIndex % 2 != 0;

            var (pos_x, pos_y, rotation, anchorage, anchor_x, anchor_y, buoy_1_connection, buoy_2_connection) = Harbours_list[harbourIndex];

            // Rebuild and replace the tuple for that specific harbour
            if (isBuoy1)
                Harbours_list[harbourIndex] = (pos_x, pos_y, rotation, anchorage, anchor_x, anchor_y, newTargetIndex, buoy_2_connection);
            else
                Harbours_list[harbourIndex] = (pos_x, pos_y, rotation, anchorage, anchor_x, anchor_y, buoy_1_connection, newTargetIndex);
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
            if (Caves_list_view.SelectedIndex != -1)
            {
                Caves_remove_button.Enabled = true;
            }
            else
            {
                Caves_remove_button.Enabled = false;
            }

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

                Cave_X_input.Value = posX;
                Cave_Y_input.Value = posY;
                Cave_type_select.SelectedIndex = type;
            }
            else
            {
                Cave_panel.Enabled = false;

                // Load default preset
                Cave_X_input.Value = 0;
                Cave_Y_input.Value = 0;
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
                (int)Cave_X_input.Value,
                (int)Cave_Y_input.Value,
                Cave_type_select.SelectedIndex
            );
        }


        private void Environment_preset_select_SelectedIndexChanged(object sender, EventArgs e)
        {
            Environment_preset_global.Enabled = true;
            Environment_preset_local.Enabled = true;
        }

        private void Environment_preset_global_Click(object sender, EventArgs e)
        {
            string selectedPreset = Environment_preset_select.SelectedItem?.ToString();

            var assembly = Assembly.GetExecutingAssembly();

            // Locate the resource ending with "{selectedPreset}.bin" (case-insensitive)
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith($"{selectedPreset}.bin", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                MessageBox.Show($"Embedded preset '{selectedPreset}' was not found in assembly resources.", "Error Loading Preset", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            using Stream preset_stream = assembly.GetManifestResourceStream(resourceName);
            byte[] environment_preset = new byte[preset_stream.Length];
            preset_stream.ReadExactly(environment_preset);
            int current_preset_byte = 0;

            int sky_texture = BitConverter.ToInt32(environment_preset, current_preset_byte);
            switch (sky_texture)
            {
                //Bavaria
                case 0:
                    Global_sky_select.SelectedIndex = 1;
                    break;
                //Starfield
                case 1:
                    Global_sky_select.SelectedIndex = 0;
                    break;
                //Egypt
                case 2:
                    Global_sky_select.SelectedIndex = 2;
                    break;
                //Bavaria
                case 3:
                    Global_sky_select.SelectedIndex = 1;
                    break;
                //Scotland
                case 4:
                    Global_sky_select.SelectedIndex = 3;
                    break;
            }
            current_preset_byte += 4;

            Global_sun_placement_input.Value = BitConverter.ToInt32(environment_preset, current_preset_byte);
            current_preset_byte += 4;

            int sun_height = BitConverter.ToInt32(environment_preset, current_preset_byte);
            if (sun_height > 100)
            {
                sun_height = 200 - sun_height;
            }
            Global_sun_height_input.Value = sun_height;
            current_preset_byte += 4;

            float[] fog_colour = new float[3];
            float[] ambient_colour = new float[3];
            float[] light_colour = new float[3];
            fog_colour[0] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            light_colour[0] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            ambient_colour[0] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            fog_colour[1] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            light_colour[1] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            ambient_colour[1] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            fog_colour[2] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            light_colour[2] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            ambient_colour[2] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            Global_fog_colour.BackColor = Color.FromArgb((int)(fog_colour[0] * 255f), (int)(fog_colour[1] * 255f), (int)(fog_colour[2] * 255f));
            Global_ambient_colour.BackColor = Color.FromArgb((int)(ambient_colour[0] * 255f), (int)(ambient_colour[1] * 255f), (int)(ambient_colour[2] * 255f));
            Global_light_colour.BackColor = Color.FromArgb((int)(light_colour[0] * 255f), (int)(light_colour[1] * 255f), (int)(light_colour[2] * 255f));

            Global_shadow_intensity_input.Value = (decimal)BitConverter.ToSingle(environment_preset, current_preset_byte) * 100;
            current_preset_byte += 4;

            Global_fog_start_input.Value = (decimal)BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            Global_fog_full_input.Value = (decimal)BitConverter.ToSingle(environment_preset, current_preset_byte);
            //current_preset_byte += 4;
        }

        private void Environment_preset_local_Click(object sender, EventArgs e)
        {
            string selectedPreset = Environment_preset_select.SelectedItem?.ToString();

            if (string.IsNullOrEmpty(selectedPreset))
            {
                MessageBox.Show("Please select an embedded preset from the list.", "No Preset Selected", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var assembly = Assembly.GetExecutingAssembly();

            // Locate the resource ending with "{selectedPreset}.bin" (case-insensitive)
            string resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(r => r.EndsWith($"{selectedPreset}.bin", StringComparison.OrdinalIgnoreCase));

            if (resourceName == null)
            {
                MessageBox.Show($"Embedded preset '{selectedPreset}' was not found in assembly resources.", "Error Loading Preset", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            if (Environment_zones.Count == 0)
            {
                Environment_add_zone_Click(sender, e);
            }

            using Stream preset_stream = assembly.GetManifestResourceStream(resourceName);
            byte[] environment_preset = new byte[preset_stream.Length];
            preset_stream.ReadExactly(environment_preset);
            //Skip sky texture, sun placement and sun height
            int current_preset_byte = 12;

            float[] fog_colour = new float[3];
            float[] ambient_colour = new float[3];
            float[] light_colour = new float[3];
            fog_colour[0] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            light_colour[0] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            ambient_colour[0] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            fog_colour[1] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            light_colour[1] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            ambient_colour[1] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            fog_colour[2] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            light_colour[2] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            ambient_colour[2] = BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            Local_fog_colour.BackColor = Color.FromArgb((int)(fog_colour[0] * 255f), (int)(fog_colour[1] * 255f), (int)(fog_colour[2] * 255f));
            Local_ambient_colour.BackColor = Color.FromArgb((int)(ambient_colour[0] * 255f), (int)(ambient_colour[1] * 255f), (int)(ambient_colour[2] * 255f));
            Local_light_colour.BackColor = Color.FromArgb((int)(light_colour[0] * 255f), (int)(light_colour[1] * 255f), (int)(light_colour[2] * 255f));

            Local_shadow_intensity_input.Value = (decimal)BitConverter.ToSingle(environment_preset, current_preset_byte) * 100;
            current_preset_byte += 4;

            Local_fog_start_input.Value = (decimal)BitConverter.ToSingle(environment_preset, current_preset_byte);
            current_preset_byte += 4;
            Local_fog_full_input.Value = (decimal)BitConverter.ToSingle(environment_preset, current_preset_byte);
            //current_preset_byte += 4;
        }

        private void Environment_preset_checkbox_CheckedChanged(object sender, EventArgs e)
        {
            if (Environment_preset_checkbox.Checked)
            {
                Environment_preset_select.Enabled = true;
                if (Environment_preset_select.SelectedIndex != -1)
                {
                    Environment_preset_global.Enabled = true;
                    Environment_preset_local.Enabled = true;
                }

                Environment_panel.Enabled = true;
            }
            else
            {
                Environment_preset_select.Enabled = false;
                Environment_preset_global.Enabled = false;
                Environment_preset_local.Enabled = false;

                Environment_panel.Enabled = false;
            }
        }

        private void Global_fog_colour_Click(object sender, EventArgs e)
        {
            ColourSelectionDialog(1, Global_fog_colour.BackColor);
        }

        private void Global_ambient_colour_Click(object sender, EventArgs e)
        {
            ColourSelectionDialog(2, Global_ambient_colour.BackColor);
        }

        private void Global_light_colour_Click(object sender, EventArgs e)
        {
            ColourSelectionDialog(3, Global_light_colour.BackColor);
        }

        private void Environment_add_zone_Click(object sender, EventArgs e)
        {
            Environment_zones.Add((
                ColorTranslator.FromHtml("#CCE6FF"), // Default fog colour
                ColorTranslator.FromHtml("#EFE5CF"), // Default ambient colour
                ColorTranslator.FromHtml("#998099"), // Default light colour
                90,                             // Default shadow intensity
                200,                            // Default fog start distance
                300,                            // Default fog full distance
                0,                              // Default X position
                0,                              // Default Y position
                0,                              // Default radius
                0                               // Default transition
            ));
            if (Environment_zones.Count == 1)
            {
                current_zone_index = 0;
            }
            LoadZoneDataToUI();
        }

        private void Environment_remove_zone_Click(object sender, EventArgs e)
        {
            if (Environment_zones.Count > 0)
            {
                Environment_zones.RemoveAt(current_zone_index);
                if (current_zone_index >= Environment_zones.Count)
                {
                    current_zone_index = Environment_zones.Count - 1;
                }
                LoadZoneDataToUI();
            }
        }

        private void Environment_previous_zone_Click(object sender, EventArgs e)
        {
            if (current_zone_index > 0)
            {
                current_zone_index--;
            }
            LoadZoneDataToUI();
        }

        private void Environment_next_zone_Click(object sender, EventArgs e)
        {
            if (current_zone_index < Environment_zones.Count - 1)
            {
                current_zone_index++;
            }
            LoadZoneDataToUI();
        }

        private void LoadZoneDataToUI()
        {
            if (current_zone_index >= 0 && current_zone_index < Environment_zones.Count)
            {
                Environment_zone_panel.Enabled = true;
                Environment_local_zones_text.Text = $"Local zones {current_zone_index + 1}/{Environment_zones.Count}";

                Environment_remove_zone.Enabled = true;
                if (current_zone_index > 0)
                {
                    Environment_previous_zone.Enabled = true;
                }
                else
                {
                    Environment_previous_zone.Enabled = false;
                }
                if (current_zone_index < Environment_zones.Count - 1)
                {
                    Environment_next_zone.Enabled = true;
                }
                else
                {
                    Environment_next_zone.Enabled = false;
                }

                var (fog_colour, ambient_colour, light_colour, shadow_intensity, fog_start_distance, fog_full_distance, pos_x, pos_y, radius, transition) = Environment_zones[current_zone_index];
                Local_fog_colour.BackColor = fog_colour;
                Local_ambient_colour.BackColor = ambient_colour;
                Local_light_colour.BackColor = light_colour;
                Local_shadow_intensity_input.Value = (decimal)shadow_intensity;
                Local_fog_start_input.Value = fog_start_distance;
                Local_fog_full_input.Value = fog_full_distance;
                Local_X_input.Value = pos_x;
                Local_Y_input.Value = pos_y;
                Local_radius_input.Value = radius;
                Local_transition_input.Value = transition;
            }
            else
            {
                Environment_zone_panel.Enabled = false;
                Environment_local_zones_text.Text = "Local zones 0/0";

                Environment_remove_zone.Enabled = false;
                Environment_previous_zone.Enabled = false;
                Environment_next_zone.Enabled = false;
            }
        }

        private void UpdateZonesList(object sender, EventArgs e)
        {
            if (current_zone_index >= 0 && current_zone_index < Environment_zones.Count)
            {
                Environment_zones[current_zone_index] = (
                    Local_fog_colour.BackColor,
                    Local_ambient_colour.BackColor,
                    Local_light_colour.BackColor,
                    (float)Local_shadow_intensity_input.Value,
                    (int)Local_fog_start_input.Value,
                    (int)Local_fog_full_input.Value,
                    (int)Local_X_input.Value,
                    (int)Local_Y_input.Value,
                    (int)Local_radius_input.Value,
                    (int)Local_transition_input.Value
                );
            }
        }

        private void Local_fog_colour_Click(object sender, EventArgs e)
        {
            ColourSelectionDialog(4, Local_fog_colour.BackColor);
        }

        private void Local_ambient_colour_Click(object sender, EventArgs e)
        {
            ColourSelectionDialog(5, Local_ambient_colour.BackColor);
        }

        private void Local_light_colour_Click(object sender, EventArgs e)
        {
            ColourSelectionDialog(6, Local_light_colour.BackColor);
        }

        private void ColourSelectionDialog(int target, Color source_color)
        {
            using HexColorDialog colorDialog = new(source_color);

            if (colorDialog.ShowDialog(this) == DialogResult.OK)
            {
                ApplySelectedColor(target, colorDialog.SelectedColor);
            }
        }

        private void ApplySelectedColor(int target, Color color)
        {
            switch (target)
            {
                case 1: Global_fog_colour.BackColor = color; break;
                case 2: Global_ambient_colour.BackColor = color; break;
                case 3: Global_light_colour.BackColor = color; break;
                case 4: Local_fog_colour.BackColor = color; UpdateZonesList(null, null); break;
                case 5: Local_ambient_colour.BackColor = color; UpdateZonesList(null, null); break;
                case 6: Local_light_colour.BackColor = color; UpdateZonesList(null, null); break;
            }
        }

        // Sacrifices amount update
        private static void UpdateUsageStatus(System.Windows.Forms.ListView listView, Label label, string factionName, int maxLimit)
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
            UpdateUsageStatus(Sacrifices_Bavarians_no_research, Sacrifices_Bavarians_no_research_usage, "Bavarians", 4);

        private void Sacrifices_no_research_Egyptians_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_Egyptians_no_research, Sacrifices_Egyptians_no_research_usage, "Egyptians", 4);

        private void Sacrifices_no_research_Scots_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_Scots_no_research, Sacrifices_Scots_no_research_usage, "Scots", 4);

        // --- Event Handlers (Research) ---

        private void Sacrifices_research_Bavarians_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_Bavarians_research, Sacrifices_Bavarians_research_usage, "Bavarians", 8);

        private void Sacrifices_research_Egyptians_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_Egyptians_research, Sacrifices_Egyptians_research_usage, "Egyptians", 8);

        private void Sacrifices_research_Scots_ItemChecked(object sender, ItemCheckedEventArgs e) =>
            UpdateUsageStatus(Sacrifices_Scots_research, Sacrifices_Scots_research_usage, "Scots", 8);

        // --- Preset Helpers ---

        private static string GetCheckedIndices(System.Windows.Forms.ListView lv)
        {
            return string.Join(",", lv.CheckedIndices.Cast<int>());
        }

        private static void SetCheckedIndices(System.Windows.Forms.ListView lv, string indicesStr)
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

        // --- Sacrifice Presets ---

        private void Sacrifice_preset_export_Click(object sender, EventArgs e)
        {
            using SaveFileDialog sfd = new() { Filter = "DnG-AdK-Mapedit sacrifice preset (*.dams)|*.dams", Title = "Export Sacrifice Preset" };
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                var lines = new List<string>
                    {
                        GetCheckedIndices(Sacrifices_Bavarians_no_research),
                        GetCheckedIndices(Sacrifices_Egyptians_no_research),
                        GetCheckedIndices(Sacrifices_Scots_no_research),
                        GetCheckedIndices(Sacrifices_Bavarians_research),
                        GetCheckedIndices(Sacrifices_Egyptians_research),
                        GetCheckedIndices(Sacrifices_Scots_research)
                    };

                File.WriteAllLines(sfd.FileName, lines);
            }
        }
        private void Sacrifice_preset_load_Click(object sender, EventArgs e)
        {
            try
            {
                string[] lines = null;

                // 1. Check if we should load from embedded resources
                if (Sacrifices_included_presets_checkbox.Checked)
                {
                    string selectedPreset = Sacrifices_included_presets_select.SelectedItem?.ToString();

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
                    using Stream stream = assembly.GetManifestResourceStream(resourceName);
                    using StreamReader reader = new(stream);
                    List<string> lineList = [];
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        lineList.Add(line);
                    }
                    lines = [.. lineList];
                }
                else
                {
                    // 2. Load from file on disk via dialog
                    using OpenFileDialog ofd = new() { Filter = "DnG-AdK-Mapedit sacrifice preset (*.dams)|*.dams", Title = "Load Sacrifice Preset" };
                    if (ofd.ShowDialog() == DialogResult.OK)
                    {
                        lines = File.ReadAllLines(ofd.FileName);
                    }
                    else
                    {
                        return; // User cancelled
                    }
                }

                // 3. Apply the preset data to controls
                if (lines != null && lines.Length >= 6)
                {
                    SetCheckedIndices(Sacrifices_Bavarians_no_research, lines[0]);
                    SetCheckedIndices(Sacrifices_Egyptians_no_research, lines[1]);
                    SetCheckedIndices(Sacrifices_Scots_no_research, lines[2]);
                    SetCheckedIndices(Sacrifices_Bavarians_research, lines[3]);
                    SetCheckedIndices(Sacrifices_Egyptians_research, lines[4]);
                    SetCheckedIndices(Sacrifices_Scots_research, lines[5]);
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
            if (Sacrifices_included_presets_checkbox.Checked)
            {
                Sacrifices_included_presets_select.Enabled = true;
            }
            else
            {
                Sacrifices_included_presets_select.Enabled = false;
            }
        }

        // --- Map Presets ---

        private void Map_preset_export_Click(object sender, EventArgs e)
        {
            using SaveFileDialog sfd = new()
            { Filter = "DnG-AdK-Mapedit map preset (*.damp)|*.damp", Title = "Export Map Preset" };
            if (sfd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    using StreamWriter sw = new(sfd.FileName);
                    sw.WriteLine("[MAP_NAME]");
                    sw.WriteLine(Map_info_name.Text);

                    sw.WriteLine("[SWAPS]");
                    foreach (var (tab, from, to) in Swap_list)
                        sw.WriteLine($"{tab},{from},{to}");

                    sw.WriteLine("[HARBOURS]");
                    foreach (var (pos_x, pos_y, rotation, anchorage, anchor_x, anchor_y, buoy_1_connection, buoy_2_connection) in Harbours_list)
                        sw.WriteLine($"{pos_x},{pos_y},{rotation},{anchorage},{anchor_x},{anchor_y},{buoy_1_connection},{buoy_2_connection}");

                    sw.WriteLine("[CAVES]");
                    foreach (var (pos_x, pos_y, type) in Caves_list)
                        sw.WriteLine($"{pos_x},{pos_y},{type}");

                    sw.WriteLine("[SACRIFICES]");
                    sw.WriteLine(GetCheckedIndices(Sacrifices_Bavarians_no_research));
                    sw.WriteLine(GetCheckedIndices(Sacrifices_Egyptians_no_research));
                    sw.WriteLine(GetCheckedIndices(Sacrifices_Scots_no_research));
                    sw.WriteLine(GetCheckedIndices(Sacrifices_Bavarians_research));
                    sw.WriteLine(GetCheckedIndices(Sacrifices_Egyptians_research));
                    sw.WriteLine(GetCheckedIndices(Sacrifices_Scots_research));

                    //Old presets use [COLOURS]
                    sw.WriteLine("[PLAYERS]");
                    sw.WriteLine(Player_count);
                    var colour_selectors = new[]
                    {
                                Players_1_colour_select, Players_2_colour_select, Players_3_colour_select,
                                Players_4_colour_select, Players_5_colour_select, Players_6_colour_select
                            };
                    sw.WriteLine(string.Join(",", colour_selectors.Take(Player_count).Select(s => s.SelectedIndex)));
                    //New
                    var difficulty_selectors = new[]
                    {
                                Players_1_difficulty_select, Players_2_difficulty_select, Players_3_difficulty_select,
                                Players_4_difficulty_select, Players_5_difficulty_select, Players_6_difficulty_select
                            };
                    sw.WriteLine(string.Join(",", difficulty_selectors.Take(Player_count).Select(s => s.SelectedIndex)));

                    sw.WriteLine("[ENVIRONMENT]");
                    sw.WriteLine(Environment_highland_water_checkbox.Checked ? "1" : "0");
                    sw.WriteLine(Environment_preset_checkbox.Checked ? "1" : "0");

                    if (Environment_preset_checkbox.Checked)
                    {
                        // Use a single interpolated string literal without '+' operators
                        sw.WriteLine(FormattableString.Invariant(
                            $"{Global_sky_select.SelectedIndex},{Global_sun_placement_input.Value},{Global_sun_height_input.Value},{Global_shadow_intensity_input.Value},{Global_fog_start_input.Value},{Global_fog_full_input.Value},{Global_fog_colour.BackColor.ToArgb()},{Global_light_colour.BackColor.ToArgb()},{Global_ambient_colour.BackColor.ToArgb()}"
                        ));

                        foreach (var (fog_colour, ambient_colour, light_colour, shadow_intensity, fog_start_distance, fog_full_distance, pos_x, pos_y, radius, transition) in Environment_zones)
                        {
                            sw.WriteLine(FormattableString.Invariant(
                                $"{fog_colour.ToArgb()},{ambient_colour.ToArgb()},{light_colour.ToArgb()},{shadow_intensity},{fog_start_distance},{fog_full_distance},{pos_x},{pos_y},{radius},{transition}"
                            ));
                        }

                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Failed to export map preset:\n" + ex.Message, "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        //Only load swaps section if Export_map_preset_swaps checkbox is checked.
        private void Map_preset_load_Click(object sender, EventArgs e)
        {
            using OpenFileDialog ofd = new() { Filter = "DnG-AdK-Mapedit map preset (*.damp)|*.damp", Title = "Load Map Preset" };
            if (ofd.ShowDialog() == DialogResult.OK)
            {
                try
                {
                    isUpdatingUI = true;

                    string[] lines = File.ReadAllLines(ofd.FileName);
                    string currentSection = "";
                    int sacrificeLine = 0;
                    int playersLine = 0;
                    int environmentLine = 0;
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
                        if (line.StartsWith('[') && line.EndsWith(']'))
                        {
                            currentSection = line;

                            if (currentSection == "[SACRIFICES]") sacrificeLine = 0;
                            if (currentSection == "[COLOURS]" || currentSection == "[PLAYERS]") playersLine = 0;
                            if (currentSection == "[ENVIRONMENT]")
                            {
                                environmentLine = 0;
                                Environment_zones.Clear();
                            }
                            continue;
                        }

                        if (Export_map_preset_swaps.Checked && currentSection != "[SWAPS]")
                        {
                            continue;
                        }

                        if (currentSection == "[MAP_NAME]")
                        {
                            if (!string.IsNullOrEmpty(line))
                            {
                                Map_info_name.Text = line;
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
                                case 0: SetCheckedIndices(Sacrifices_Bavarians_no_research, line); break;
                                case 1: SetCheckedIndices(Sacrifices_Egyptians_no_research, line); break;
                                case 2: SetCheckedIndices(Sacrifices_Scots_no_research, line); break;
                                case 3: SetCheckedIndices(Sacrifices_Bavarians_research, line); break;
                                case 4: SetCheckedIndices(Sacrifices_Egyptians_research, line); break;
                                case 5: SetCheckedIndices(Sacrifices_Scots_research, line); break;
                            }
                            sacrificeLine++;
                        }
                        else if (currentSection == "[COLOURS]" || currentSection == "[PLAYERS]")
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            if (playersLine == 0)
                            {
                                // Line 0: Saved Player Count
                                if (!int.TryParse(line, out savedPlayerCount))
                                {
                                    MessageBox.Show("Failed to parse saved player count.", "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
                                    return;
                                }
                                playersLine++;
                            }
                            else if (playersLine == 1)
                            {
                                // Line 1: Player Colors
                                if (savedPlayerCount == Player_count)
                                {
                                    var selectors = new[]
                                    {
                                Players_1_colour_select, Players_2_colour_select, Players_3_colour_select,
                                Players_4_colour_select, Players_5_colour_select, Players_6_colour_select
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
                                playersLine++;
                            }
                            else if (playersLine == 2 && currentSection == "[PLAYERS]")
                            {
                                // Line 2: Player Difficulties (New section only)
                                if (savedPlayerCount == Player_count)
                                {
                                    var selectors = new[]
                                    {
                                Players_1_difficulty_select, Players_2_difficulty_select, Players_3_difficulty_select,
                                Players_4_difficulty_select, Players_5_difficulty_select, Players_6_difficulty_select
                            };

                                    var parts = line.Split(',');
                                    int maxPlayers = Math.Min(parts.Length, Math.Min(Player_count, selectors.Length));

                                    for (int i = 0; i < maxPlayers; i++)
                                    {
                                        if (int.TryParse(parts[i].Trim(), out int diffIdx) &&
                                            diffIdx >= 0 &&
                                            diffIdx < selectors[i].Items.Count)
                                        {
                                            selectors[i].SelectedIndex = diffIdx;
                                        }
                                    }
                                }
                                playersLine++;
                            }
                        }
                        else if (currentSection == "[ENVIRONMENT]")
                        {
                            if (string.IsNullOrWhiteSpace(line)) continue;

                            if (environmentLine == 0)
                            {
                                // Line 0: Highland Water Checkbox
                                Environment_highland_water_checkbox.Checked = line == "1";
                                environmentLine++;
                            }
                            else if (environmentLine == 1)
                            {
                                // Line 1: Preset Checkbox
                                Environment_preset_checkbox.Checked = line == "1";
                                environmentLine++;
                            }
                            else if (environmentLine == 2)
                            {
                                // Line 2: Global Environment Settings
                                if (Environment_preset_checkbox.Checked)
                                {
                                    var parts = SplitCsvWithBrackets(line);
                                    if (parts.Length >= 9)
                                    {
                                        // [0] Sky Selection Index
                                        if (int.TryParse(parts[0], out int skyIdx) && skyIdx >= 0 && skyIdx < Global_sky_select.Items.Count)
                                            Global_sky_select.SelectedIndex = skyIdx;

                                        // [1] Sun Placement
                                        if (decimal.TryParse(parts[1], System.Globalization.CultureInfo.InvariantCulture, out decimal sunP) || decimal.TryParse(parts[1], out sunP))
                                            Global_sun_placement_input.Value = Math.Clamp(sunP, Global_sun_placement_input.Minimum, Global_sun_placement_input.Maximum);

                                        // [2] Sun Height
                                        if (decimal.TryParse(parts[2], System.Globalization.CultureInfo.InvariantCulture, out decimal sunH) || decimal.TryParse(parts[2], out sunH))
                                            Global_sun_height_input.Value = Math.Clamp(sunH, Global_sun_height_input.Minimum, Global_sun_height_input.Maximum);

                                        // [3] Shadow Intensity
                                        if (decimal.TryParse(parts[3], System.Globalization.CultureInfo.InvariantCulture, out decimal shadowI) || decimal.TryParse(parts[3], out shadowI))
                                            Global_shadow_intensity_input.Value = Math.Clamp(shadowI, Global_shadow_intensity_input.Minimum, Global_shadow_intensity_input.Maximum);

                                        // [4] Fog Start
                                        if (decimal.TryParse(parts[4], System.Globalization.CultureInfo.InvariantCulture, out decimal fogS) || decimal.TryParse(parts[4], out fogS))
                                            Global_fog_start_input.Value = Math.Clamp(fogS, Global_fog_start_input.Minimum, Global_fog_start_input.Maximum);

                                        // [5] Fog Full
                                        if (decimal.TryParse(parts[5], System.Globalization.CultureInfo.InvariantCulture, out decimal fogF) || decimal.TryParse(parts[5], out fogF))
                                            Global_fog_full_input.Value = Math.Clamp(fogF, Global_fog_full_input.Minimum, Global_fog_full_input.Maximum);

                                        // [6] Fog Color, [7] Light Color, [8] Ambient Color
                                        Global_fog_colour.BackColor = ParseColor(parts[6]);
                                        Global_light_colour.BackColor = ParseColor(parts[7]);
                                        Global_ambient_colour.BackColor = ParseColor(parts[8]);
                                    }
                                }
                                environmentLine++;
                            }
                            else
                            {
                                // Line 3+: Environment Zones
                                if (Environment_preset_checkbox.Checked)
                                {
                                    var parts = SplitCsvWithBrackets(line);
                                    if (parts.Length >= 10)
                                    {
                                        Color fogCol = ParseColor(parts[0]);
                                        Color ambCol = ParseColor(parts[1]);
                                        Color lightCol = ParseColor(parts[2]);

                                        if (ParseInt(parts[3], out int shadowIntensity) &&
                                            ParseInt(parts[4], out int fogStartDist) &&
                                            ParseInt(parts[5], out int fogFullDist) &&
                                            ParseInt(parts[6], out int posX) &&
                                            ParseInt(parts[7], out int posY) &&
                                            ParseInt(parts[8], out int radius) &&
                                            ParseInt(parts[9], out int transition))
                                        {
                                            Environment_zones.Add((fogCol, ambCol, lightCol, shadowIntensity, fogStartDist, fogFullDist, posX, posY, radius, transition));
                                        }
                                    }
                                }
                                environmentLine++;
                            }
                        }
                    }

                    isUpdatingUI = false;

                    UpdateBuoyDropdownItems();

                    if (Harbours_list.Count > 0) Harbours_list_view.SelectedIndex = 0;
                    else UpdateHarbourPanel();

                    if (Caves_list.Count > 0) Caves_list_view.SelectedIndex = 0;
                    else UpdateCavePanel();

                    if (Environment_zones.Count > 0)
                    {
                        current_zone_index = 0;
                        LoadZoneDataToUI();
                    }
                }
                catch (Exception ex)
                {
                    isUpdatingUI = false;
                    MessageBox.Show("Failed to load map preset. The file might be corrupted.\n" + ex.Message, "Error Loading Map Preset", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private static string[] SplitCsvWithBrackets(string input)
        {
            List<string> result = [];
            int bracketDepth = 0;
            int startIndex = 0;

            for (int i = 0; i < input.Length; i++)
            {
                if (input[i] == '[') bracketDepth++;
                else if (input[i] == ']') { if (bracketDepth > 0) bracketDepth--; }
                else if (input[i] == ',' && bracketDepth == 0)
                {
                    result.Add(input[startIndex..i].Trim());
                    startIndex = i + 1;
                }
            }

            if (startIndex <= input.Length)
            {
                result.Add(input[startIndex..].Trim());
            }

            return [.. result];
        }

        private static Color ParseColor(string colorStr)
        {
            if (string.IsNullOrWhiteSpace(colorStr)) return Color.Black;

            // Parse integer ARGB values
            if (int.TryParse(colorStr, out int argb))
            {
                return Color.FromArgb(argb);
            }

            // Fallback for legacy color format
            try
            {
                string clean = colorStr.Replace("Color", "").Replace("[", "").Replace("]", "").Trim();
                var parts = clean.Split(',');
                int a = 255, r = 0, g = 0, b = 0;
                bool parsedAny = false;

                foreach (var part in parts)
                {
                    var kv = part.Split('=');
                    if (kv.Length == 2)
                    {
                        string key = kv[0].Trim().ToUpperInvariant();
                        if (int.TryParse(kv[1].Trim(), out int val))
                        {
                            if (key == "A") { a = val; parsedAny = true; }
                            else if (key == "R") { r = val; parsedAny = true; }
                            else if (key == "G") { g = val; parsedAny = true; }
                            else if (key == "B") { b = val; parsedAny = true; }
                        }
                    }
                }

                if (parsedAny) return Color.FromArgb(a, r, g, b);
            }
            catch { }

            return Color.Black;
        }

        private static bool ParseInt(string input, out int value)
        {
            if (int.TryParse(input, out value)) return true;
            if (decimal.TryParse(input, System.Globalization.CultureInfo.InvariantCulture, out decimal d))
            {
                value = (int)Math.Round(d);
                return true;
            }
            if (decimal.TryParse(input, out d))
            {
                value = (int)Math.Round(d);
                return true;
            }
            value = 0;
            return false;
        }


        private void Export_forester_fix_LinkClicked(object sender, LinkLabelLinkClickedEventArgs e)
        {
            Export_forester_fix.LinkVisited = true;

            // Open the URL
            Process.Start(new ProcessStartInfo
            {
                FileName = "https://www.moddb.com/games/the-settlers-rise-of-cultures/downloads/forester-crash-fix",
                UseShellExecute = true
            });
        }

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
                if (Harbours_list[i].buoy_1_connection <= 0 && Harbours_list[i].buoy_2_connection <= 0)
                {
                    MessageBox.Show($"Harbour #{i + 1} does not have any connections.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            //Ships are only built at a shipyard, which needs anchorage ground
            if (Harbours_list.Count > 0 && !Harbours_list.Any(h => h.anchorage))
            {
                if (MessageBox.Show("No harbour has an anchorage, so no shipyard can be built and the harbours will never be used. Export anyway?", "No anchorage", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                {
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

            //Check if all zones are at least partially in-bounds and if the maximum radius indluding transition is not excedded.
            int max_radius = (int)Math.Ceiling(Math.Sqrt(map_size_x * map_size_x + map_size_y * map_size_y));
            for (int i = 0; i < Environment_zones.Count; i++)
            {
                var (fog_colour, ambient_colour, light_colour, shadow_intensity, fog_start_distance, fog_full_distance, pos_x, pos_y, radius, transition) = Environment_zones[i];
                int effectiveRadius = radius + transition;
                if (effectiveRadius > max_radius)
                {
                    MessageBox.Show($"Environment Zone #{i + 1} has an effective radius ({effectiveRadius}) that exceeds the maximum allowed ({max_radius}).", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }

                // Find the closest point on the rectangle to the circle's center
                int closestX = Math.Clamp(pos_x, 0, map_size_x);
                int closestY = Math.Clamp(pos_y, 0, map_size_y);

                // Calculate distance delta
                int deltaX = pos_x - closestX;
                int deltaY = pos_y - closestY;

                // Compare squared distance against squared radius to avoid costly square root operations
                double distanceSquared = (double)deltaX * deltaX + (double)deltaY * deltaY;
                double radiusSquared = (double)effectiveRadius * effectiveRadius;

                if (distanceSquared > radiusSquared)
                {
                    MessageBox.Show($"Environment Zone #{i + 1} is completely out of bounds.", "Validation Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            //Check if 2 players don't have the same default colour
            var colourSelectors = new[]
            {
                Players_1_colour_select, Players_2_colour_select, Players_3_colour_select,
                Players_4_colour_select, Players_5_colour_select, Players_6_colour_select
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

            //Check if the sacrifice amount limits are not crossed
            var sacrificeChecks = new (System.Windows.Forms.ListView lv, string name, int max)[]
            {
                (Sacrifices_Bavarians_no_research, "Bavarians (No Research)", 4),
                (Sacrifices_Egyptians_no_research, "Egyptians (No Research)", 4),
                (Sacrifices_Scots_no_research, "Scots (No Research)", 4),
                (Sacrifices_Bavarians_research, "Bavarians (Research)", 8),
                (Sacrifices_Egyptians_research, "Egyptians (Research)", 8),
                (Sacrifices_Scots_research, "Scots (Research)", 8)
            };

            foreach (var (lv, name, max) in sacrificeChecks)
            {
                if (lv.CheckedItems.Count > max)
                {
                    MessageBox.Show($"Sacrifice limit exceeded for {name}. Maximum allowed is {max}.", "Sacrifice limits crossed", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }

            //In this case do not allow the user to proceed
            if (Map_info_name.Text.Length > 20)
            {
                MessageBox.Show("Current map name with a length of " + Map_info_name.Text.Length + " characters is larger than the maximum allowed of 20 characters", "Map name is too long", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            SaveFileDialog saveFileDialog = new();

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

                    if (Export_multiplayer_prefix.Checked)
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
            int[] selectedColours = [.. colourSelectors.Select(s => s.SelectedIndex)];
            int[] selectedDifficulties = [.. new[]
            {
                Players_1_difficulty_select.SelectedIndex,
                Players_2_difficulty_select.SelectedIndex,
                Players_3_difficulty_select.SelectedIndex,
                Players_4_difficulty_select.SelectedIndex,
                Players_5_difficulty_select.SelectedIndex,
                Players_6_difficulty_select.SelectedIndex
            }];

            int[] bavariansNoRes = [.. Sacrifices_Bavarians_no_research.CheckedIndices.Cast<int>()];
            int[] bavariansRes = [.. Sacrifices_Bavarians_research.CheckedIndices.Cast<int>()];
            int[] egyptiansNoRes = [.. Sacrifices_Egyptians_no_research.CheckedIndices.Cast<int>()];
            int[] egyptiansRes = [.. Sacrifices_Egyptians_research.CheckedIndices.Cast<int>()];
            int[] scotsNoRes = [.. Sacrifices_Scots_no_research.CheckedIndices.Cast<int>()];
            int[] scotsRes = [.. Sacrifices_Scots_research.CheckedIndices.Cast<int>()];

            Export_wait.Visible = true;
            Tab_control.Enabled = false;

            try
            {
                byte[] exportedMap = await Task.Run(() => MapExportScript(
                    selectedColours, selectedDifficulties,
                    bavariansNoRes, bavariansRes,
                    egyptiansNoRes, egyptiansRes,
                    scotsNoRes, scotsRes
                ));

                if (exportedMap != null)
                {
                    string tempFile = Path.Combine(TempFolder, Path.GetFileName(saveFileDialog.FileName));
                    File.WriteAllBytes(tempFile, exportedMap);

                    if (Environment_preset_checkbox.Checked)
                    {
                        byte[] fogFile = FogFileExport();
                        File.WriteAllBytes(Path.ChangeExtension(tempFile, ".bin"), fogFile);
                    }

                    if (Export_multiplayer_prefix.Checked)
                    {
                        string directory = Path.GetDirectoryName(saveFileDialog.FileName);
                        string baseFileName = Path.GetFileNameWithoutExtension(saveFileDialog.FileName);
                        string extension = Path.GetExtension(saveFileDialog.FileName);

                        destinationFileName = Path.Combine(directory, $"MP_{Player_count}P_{baseFileName.Replace(' ', '_').ToLowerInvariant()}{extension}");
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
            finally
            {
                Export_wait.Visible = false;
                Tab_control.Enabled = true;
            }
        }

        private byte[] FogFileExport()
        {
            MemoryStream fog_file = new();
            using (BinaryWriter w = new(fog_file))
            {
                //Sky texture
                switch (Global_sky_select.SelectedIndex)
                {
                    case 0: //Starlight
                        w.Write([0x01, 0x00, 0x00, 0x00]);
                        break;
                    case 1: // Bavarian
                        w.Write([0x03, 0x00, 0x00, 0x00]);
                        break;
                    case 2: // Egyptian
                        w.Write([0x02, 0x00, 0x00, 0x00]);
                        break;
                    case 3: // Scottish
                        w.Write([0x04, 0x00, 0x00, 0x00]);
                        break;
                }

                //Sun position
                w.Write((int)Global_sun_placement_input.Value);
                //Sun height
                w.Write((int)Global_sun_height_input.Value);

                Color fog_colour = Global_fog_colour.BackColor;
                Color ambient_colour = Global_ambient_colour.BackColor;
                Color light_colour = Global_light_colour.BackColor;

                w.Write(fog_colour.R / 255f);
                w.Write(light_colour.R / 255f);
                w.Write(ambient_colour.R / 255f);

                w.Write(fog_colour.G / 255f);
                w.Write(light_colour.G / 255f);
                w.Write(ambient_colour.G / 255f);

                w.Write(fog_colour.B / 255f);
                w.Write(light_colour.B / 255f);
                w.Write(ambient_colour.B / 255f);

                //Shadow intensity
                w.Write((float)Global_shadow_intensity_input.Value / 100f);

                //Fog start distance
                w.Write((float)Global_fog_start_input.Value);
                //Full fog distance
                w.Write((float)Global_fog_full_input.Value);

                //Local zones
                w.Write(Environment_zones.Count);
                foreach (var zone in Environment_zones)
                {
                    fog_colour = zone.fog_colour;
                    ambient_colour = zone.ambient_colour;
                    light_colour = zone.light_colour;

                    w.Write(fog_colour.R / 255f);
                    w.Write(ambient_colour.R / 255f);
                    w.Write(light_colour.R / 255f);

                    w.Write(fog_colour.G / 255f);
                    w.Write(ambient_colour.G / 255f);
                    w.Write(light_colour.G / 255f);

                    w.Write(fog_colour.B / 255f);
                    w.Write(ambient_colour.B / 255f);
                    w.Write(light_colour.B / 255f);

                    //Shadow intensity
                    w.Write((float)zone.shadow_intensity / 100f);

                    //Fog start distance
                    w.Write((float)zone.fog_start_distance);
                    //Full fog distance
                    w.Write((float)zone.fog_full_distance);

                    //Position relative to centre
                    if (zone.pos_y % 2 == 0)
                    {
                        w.Write((float)(zone.pos_x * 4 - map_size_x * 2));
                    }
                    else
                    {
                        w.Write((float)(zone.pos_x * 4 + 2 - map_size_x * 2));
                    }
                    w.Write((float)(zone.pos_y * 4 - map_size_y * 2));

                    //Radius
                    w.Write((float)(zone.radius * 4));

                    //Radius + transition
                    w.Write((float)((zone.radius + zone.transition) * 4));
                }
            }
            return fog_file.ToArray();
        }

        // Pre-parsed static inverted byte arrays for sacrifice items
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
            int[] selectedColours, int[] selectedDifficulties,
            int[] bavariansNoRes, int[] bavariansRes,
            int[] egyptiansNoRes, int[] egyptiansRes,
            int[] scotsNoRes, int[] scotsRes)
        {
            byte[] DnG_map = File.ReadAllBytes(WorkingFileName);
            int current_dng_byte = 0;
            int format_version = BitConverter.ToInt32(DnG_map, current_dng_byte);

            // Read template directly into MemoryStream
            Assembly assembly = Assembly.GetExecutingAssembly();
            MemoryStream adk_memory_stream = new();
            using (Stream stream = assembly.GetManifestResourceStream("DnG_AdK_Mapedit.MP_6P_snowflake.s2m"))
            {
                stream.CopyTo(adk_memory_stream);
            }

            int template_map_area = 110 * 110;

            //Skip to the player count byte
            current_dng_byte += 12;
            int current_adk_byte = 24;

            //Overwrite player count
            int template_map_players = BitConverter.ToInt32(adk_memory_stream.ToArray(), current_adk_byte);
            adk_memory_stream.Position = current_adk_byte;
            adk_memory_stream.Write(BitConverter.GetBytes(Player_count), 0, 4);
            current_dng_byte += 4;
            current_adk_byte += 4;

            //Overwrite start positions (template map has 6 players)
            int start_positions_data_length = 20 * Player_count;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 120, DnG_map, current_dng_byte, start_positions_data_length);
            current_adk_byte += start_positions_data_length;
            current_dng_byte += start_positions_data_length;

            // 1. Read source length and total byte count from DnG map
            int dng_map_name_length = BitConverter.ToInt32(DnG_map, current_dng_byte);
            int dng_map_name_length_total = dng_map_name_length + 4;

            // 2. Read existing target length from ADK stream to know how many bytes to remove
            adk_memory_stream.Position = current_adk_byte;
            byte[] adkLengthBuffer = new byte[4];
            adk_memory_stream.ReadExactly(adkLengthBuffer);

            int adk_map_name_length = BitConverter.ToInt32(adkLengthBuffer, 0);
            int adk_bytes_to_replace = adk_map_name_length + 4; // Old length prefix (4) + old string

            // 3. Replace the exact length of the old section in ADK with the new section from DnG
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, adk_bytes_to_replace, DnG_map, current_dng_byte, dng_map_name_length_total);

            // 4. Advance offsets by what was read / written
            current_adk_byte += dng_map_name_length_total;
            current_dng_byte += dng_map_name_length_total;

            //Overwrite map dimensions
            adk_memory_stream.Position = current_adk_byte;
            adk_memory_stream.Write(BitConverter.GetBytes(map_size_x), 0, 4);
            adk_memory_stream.Write(BitConverter.GetBytes(map_size_y), 0, 4);
            current_adk_byte += 8;
            current_dng_byte += 8;

            //Overwrite player types
            adk_memory_stream.Position = current_adk_byte;
            adk_memory_stream.Write([0x02, 0x00, 0x00, 0x00], 0, 4);
            current_adk_byte += 4;

            for (int i = 2; i <= 8; i++)
            {
                byte[] typeBytes = (i > Player_count) ? [0, 0, 0, 0] : [1, 0, 0, 0];
                adk_memory_stream.Write(typeBytes, 0, 4);
                current_adk_byte += 4;
            }
            current_dng_byte += 32;

            //Player colours and difficulty
            for (int i = 1; i <= 8; i++)
            {
                //Skip scripted map nations
                current_adk_byte += 4;

                //Write player colours
                adk_memory_stream.Position = current_adk_byte;
                int color = (i <= Player_count) ? selectedColours[i - 1] : (i - 1);
                adk_memory_stream.Write(BitConverter.GetBytes(color), 0, 4);
                current_adk_byte += 8; // Include skipped scripted map player teams

                //Write difficulty levels
                adk_memory_stream.Position = current_adk_byte;
                byte[] difficulty = (i == 1 || i > Player_count) ? [0, 0, 0, 0] : BitConverter.GetBytes(selectedDifficulties[i - 1]);
                adk_memory_stream.Write(difficulty, 0, 4);
                current_adk_byte += 4;
            }
            current_dng_byte += 128;

            //Overwrite water shader type
            if (Environment_highland_water_checkbox.Checked)
            {
                adk_memory_stream.Position = current_adk_byte;
                adk_memory_stream.Write([0x01, 0x00, 0x00, 0x00], 0, 4);
            }
            else
            {

                adk_memory_stream.Position = current_adk_byte;
                int water_type = BitConverter.ToInt32(DnG_map, current_dng_byte);
                if (water_type > 0)
                {
                    adk_memory_stream.Write(BitConverter.GetBytes(water_type + 1), 0, 4);
                }
                else
                {
                    adk_memory_stream.Write([0x00, 0x00, 0x00, 0x00], 0, 4);
                }
            }
            current_dng_byte += 4;
            current_adk_byte += 4;

            //Skip to the UUID
            current_dng_byte += 24;
            current_adk_byte += 24;
            //Each exported map needs its own UUID, the lobby identifies maps by it
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 16, Guid.NewGuid().ToByteArray());
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
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 124, [0, 0, 0, 0]);
            current_adk_byte += 4;

            //Skip scripted map start resources and static 4 bytes before it
            current_adk_byte += 564;

            // Sacrifices Section
            byte[] sacBytes;
            using (MemoryStream sacrificeData = new())
            using (BinaryWriter writer = new(sacrificeData))
            {
                writer.Write(3); // 3 nations

                // Bavarians (.Reverse() matches the old InsertRange LIFO behavior)
                writer.Write([0xA3, 0x78, 0xD3, 0xB0]);
                writer.Write(bavariansNoRes.Length + bavariansRes.Length);
                foreach (int idx in bavariansNoRes.Reverse()) writer.Write(BavariansNoResearch[idx]);
                foreach (int idx in bavariansRes.Reverse()) writer.Write(BavariansResearch[idx]);

                // Egyptians
                writer.Write([0x33, 0x6D, 0x01, 0xF5]);
                writer.Write(egyptiansNoRes.Length + egyptiansRes.Length);
                foreach (int idx in egyptiansNoRes.Reverse()) writer.Write(EgyptiansNoResearch[idx]);
                foreach (int idx in egyptiansRes.Reverse()) writer.Write(EgyptiansResearch[idx]);

                // Scots
                writer.Write([0xA3, 0xFD, 0x7F, 0x49]);
                writer.Write(scotsNoRes.Length + scotsRes.Length);
                foreach (int idx in scotsNoRes.Reverse()) writer.Write(ScotsNoResearch[idx]);
                foreach (int idx in scotsRes.Reverse()) writer.Write(ScotsResearch[idx]);

                sacBytes = sacrificeData.ToArray();
            }

            //Overwrite sacrifices section
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 140, sacBytes);
            current_adk_byte += sacBytes.Length;

            //Skip to ID
            current_dng_byte += 12;
            current_adk_byte += 12;
            //Overwrite ID counter, new IDs are taken from it and the final value is written at the end
            int unique_id_counter_offset = current_adk_byte;
            next_unique_id = BitConverter.ToInt64(DnG_map, current_dng_byte);
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 8, DnG_map, current_dng_byte, 8);
            current_adk_byte += 8;
            current_dng_byte += 8;

            //Skip to a map size section with unknown use
            current_adk_byte += 36;
            //Overwrite map dimensions
            adk_memory_stream.Position = current_adk_byte;
            adk_memory_stream.Write(BitConverter.GetBytes(map_size_x), 0, 4);
            adk_memory_stream.Write(BitConverter.GetBytes(map_size_y), 0, 4);
            current_adk_byte += 8;

            //Skip to the end of the heightmap header
            current_adk_byte += 20;
            current_dng_byte = FindSequenceOffset(DnG_map, HeightsHeader, current_dng_byte);

            //Overwrite heightmap dimensions
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 8, DnG_map, current_dng_byte, 8);
            current_adk_byte += 8;

            int heightmap_size_x = BitConverter.ToInt32(DnG_map, current_dng_byte);
            int heightmap_size_y = BitConverter.ToInt32(DnG_map, current_dng_byte + 4);
            current_dng_byte += 8;

            //Overwrite heightmap data
            int heightmap_data_length = heightmap_size_x * heightmap_size_y * 4;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 777924, DnG_map, current_dng_byte, heightmap_data_length); //(441*441*4)
            current_dng_byte += heightmap_data_length;

            int map_area = map_size_x * map_size_y;
            int[,] heightmap_logical = new int[map_size_y, map_size_x];

            // Create a heightmap that uses only logical coordinates
            byte[] adk_byte_array = adk_memory_stream.ToArray(); // Fetch stream array buffer once

            if (Harbours_list.Count > 0)
            {
                for (int i = 0; i < map_area; i++)
                {
                    // Column-first indexing (index increases down each column source_y, then moves to the next column source_x)
                    int x_logical = i / map_size_y;
                    int y_logical = i % map_size_y;
                    int x_detailed = (y_logical % 2 == 0) ? (x_logical * 4) : ((x_logical * 4) + 2);
                    int y_detailed = y_logical * 4;

                    int targetIndex = x_detailed + (y_detailed * heightmap_size_x);
                    int byteOffset = current_adk_byte + (targetIndex * 4);

                    if (byteOffset >= 0 && byteOffset + 4 <= adk_byte_array.Length)
                    {
                        //Swap source_x and source_y coordinates
                        heightmap_logical[y_logical, x_logical] = BitConverter.ToInt32(adk_byte_array, byteOffset);
                    }
                }
            }
            current_adk_byte += heightmap_data_length;

            //Skip the textures header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Overwrite map area
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 4, BitConverter.GetBytes(map_area));
            current_adk_byte += 4;
            current_dng_byte += 4;
            //Overwrite texture data
            int textures_beginning = current_adk_byte;
            int textures_data_length = map_area * 4;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, template_map_area * 4, DnG_map, current_dng_byte, textures_data_length);
            current_dng_byte += textures_data_length;
            current_adk_byte += textures_data_length;

            //Skip gridstate map header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Overwrite map area
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 4, BitConverter.GetBytes(map_area));
            current_adk_byte += 4;
            current_dng_byte += 4;
            //Overwrite gridstate data (length should be the same as the texture data)
            int gridstates_beginning = current_adk_byte;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, template_map_area * 4, DnG_map, current_dng_byte, textures_data_length);
            current_dng_byte += textures_data_length;

            adk_byte_array = adk_memory_stream.ToArray();

            // Strip invalid in AdK harbour flags (byte 2, 0x10) from initial gridstate
            int gridstateOffsetTemp = gridstates_beginning;
            for (int i = 0; i < map_area; i++)
            {
                gridstateOffsetTemp += 1; // Byte 2
                adk_byte_array[gridstateOffsetTemp] &= 0xEF;
                gridstateOffsetTemp += 3; // Advance to next tile Byte 1
            }

            // Block hexagons occupied by caves (byte 2, 0x04)
            foreach (var (pos_x, pos_y, type) in Caves_list)
            {
                int caveByteIndex = gridstates_beginning + (pos_y * map_size_x + pos_x) * 4 + 1;
                adk_byte_array[caveByteIndex] |= 0x04;
            }

            // Texture Swapping Loop
            foreach (var (tab, from, to) in Swap_list)
            {
                if (tab == 1)
                {
                    int texture_from = BitConverter.ToInt32(DnG_textures[from], 0);
                    byte[] texture_to = AdK_textures[to];

                    for (int j = 0; j < map_area; j++)
                    {
                        int textureOffset = textures_beginning + j * 4;
                        if (BitConverter.ToInt32(adk_byte_array, textureOffset) == texture_from)
                        {
                            Buffer.BlockCopy(texture_to, 0, adk_byte_array, textureOffset, 4);
                        }
                    }
                }
            }

            // --- ANCHORAGE TEXTURE & GRIDSTATE CODE ---
            byte[] pavementTexture = [0x01, 0xDE, 0xCA, 0xDE];

            for (int i = 0; i < Harbours_list.Count; i++)
            {
                if (Harbours_list[i].anchorage)
                {
                    int anchorIndex = Harbours_list[i].anchor_y * map_size_x + Harbours_list[i].anchor_x;

                    //Replace a texture under the anchorage
                    int anchorTextureOffset = textures_beginning + (anchorIndex * 4);
                    Buffer.BlockCopy(pavementTexture, 0, adk_byte_array, anchorTextureOffset, 4);

                    //Validate coastal placement
                    int anchorGridstateOffset = gridstates_beginning + (anchorIndex * 4);

                    if ((adk_byte_array[anchorGridstateOffset] & 0x08) == 0)
                    {
                        MessageBox.Show($"Harbour at index {i} has an anchor in an invalid location.", "Anchor is in invalid location", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return null;
                    }
                }
            }

            //Derive blocked, mining, building and ship ground flags from the final textures
            uint[] gridstates = S2mRules.ReadUInts(adk_byte_array, gridstates_beginning, map_area);
            List<int> resources_to_clear = S2mRules.RecomputePatternBits(gridstates, S2mRules.ReadUInts(adk_byte_array, textures_beginning, map_area));
            S2mRules.WriteUInts(gridstates, adk_byte_array, gridstates_beginning);

            //Ships can't pass spawns and blocking doodads in the water
            if (Harbours_list.Count > 0)
            {
                for (int i = 0; i < map_area; i++)
                {
                    if ((gridstates[i] & S2mRules.LogicObject) != 0)
                        heightmap_logical[i / map_size_x, i % map_size_x] = 0;
                }
            }

            adk_memory_stream.Position = 0;
            adk_memory_stream.Write(adk_byte_array, 0, adk_byte_array.Length);

            current_adk_byte = gridstates_beginning + textures_data_length;

            //Skip resource map header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Overwrite map dimensions
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 8, BitConverter.GetBytes(map_size_x));
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte + 4, 0, BitConverter.GetBytes(map_size_y));
            current_adk_byte += 8; current_dng_byte += 8;
            //Overwrite resources array
            int resources_data_length = map_area * 8;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, template_map_area * 8, DnG_map, current_dng_byte, resources_data_length);
            foreach (int i in resources_to_clear)
            {
                ReplaceStreamBytes(adk_memory_stream, current_adk_byte + i * 8, 8, [0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF]);
            }
            current_dng_byte += resources_data_length;
            current_adk_byte += resources_data_length;

            //Skip territory map header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Overwrite map dimensions
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 8, BitConverter.GetBytes(map_size_x));
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte + 4, 0, BitConverter.GetBytes(map_size_y));
            current_adk_byte += 8; current_dng_byte += 8;
            //Overwrite territory map data (length should be the same as the texture data)
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, template_map_area * 4, DnG_map, current_dng_byte, textures_data_length);
            current_dng_byte += textures_data_length;
            current_adk_byte += textures_data_length;

            //Skip exploration map header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Overwrite map dimensions
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 8, BitConverter.GetBytes(map_size_x));
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte + 4, 0, BitConverter.GetBytes(map_size_y));
            current_adk_byte += 8; current_dng_byte += 8;
            //Overwrite exploration map data
            int exploration_map_length = map_area * 32;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, template_map_area * 32, DnG_map, current_dng_byte, exploration_map_length);
            current_dng_byte += exploration_map_length;
            current_adk_byte += exploration_map_length;

            //Copy the continents map and the resources header, the continents are recomputed at the end
            byte[] depositsHeaderDng = [0x04, 0x00, 0x00, 0x00, 0xAE, 0xEB, 0x66, 0xEF, 0x09, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
            byte[] depositsHeaderAdk = [0x06, 0x00, 0x00, 0x00, 0xAE, 0xEB, 0x66, 0xEF, 0x09, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];

            adk_byte_array = adk_memory_stream.ToArray();
            int depositsOffsetDng = FindSequenceOffset(DnG_map, depositsHeaderDng, current_dng_byte);
            int depositsOffsetAdk = FindSequenceOffset(adk_byte_array, depositsHeaderAdk, current_adk_byte);

            int to_copy_length = depositsOffsetDng - current_dng_byte;
            int continents_beginning = current_adk_byte;
            int continents_length = to_copy_length - depositsHeaderDng.Length;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, depositsOffsetAdk - current_adk_byte, DnG_map, current_dng_byte, to_copy_length);
            current_dng_byte = depositsOffsetDng;
            current_adk_byte += to_copy_length;

            //Overwrite deposits array length
            int deposits_beginning = current_adk_byte;

            int deposits_amount = BitConverter.ToInt32(DnG_map, current_dng_byte);
            current_dng_byte += 4;
            int deposits_amount_adk = BitConverter.ToInt32(adk_memory_stream.ToArray(), current_adk_byte);
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 4, BitConverter.GetBytes(deposits_amount));
            current_adk_byte += 4;
            //Overwrite the deposits array data
            int depositsDataLength = deposits_amount * 108;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, deposits_amount_adk * 108, DnG_map, current_dng_byte, depositsDataLength);
            current_dng_byte += depositsDataLength;
            current_adk_byte += depositsDataLength;

            //For now just overwrite the animals array without modifing the source
            int animals_beginning = current_adk_byte;
            int animals_amount = BitConverter.ToInt32(DnG_map, current_dng_byte);

            byte[] doodadsHeader = [0x00, 0x00, 0x00, 0x00, 0x3C, 0xCC, 0xBC, 0x8E, 0x0D, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];
            adk_byte_array = adk_memory_stream.ToArray();
            int doodadsOffsetDng = FindSequenceOffset(DnG_map, doodadsHeader, current_dng_byte);
            int doodadsOffsetAdk = FindSequenceOffset(adk_byte_array, doodadsHeader, current_adk_byte);

            to_copy_length = doodadsOffsetDng - current_dng_byte;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, doodadsOffsetAdk - current_adk_byte, DnG_map, current_dng_byte, to_copy_length);
            current_dng_byte = doodadsOffsetDng;
            current_adk_byte += to_copy_length;

            //Overwrite doodads array length
            int doodads_beginning = current_adk_byte;
            int doodads_amount = BitConverter.ToInt32(DnG_map, current_dng_byte);
            current_dng_byte += 4;
            int doodads_amount_adk = BitConverter.ToInt32(adk_memory_stream.ToArray(), current_adk_byte);

            int doodads_data_length = doodads_amount * 56;
            doodads_amount += Harbours_list.Count(h => h.anchorage);
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 4, BitConverter.GetBytes(doodads_amount));
            current_adk_byte += 4;
            //Overwrite doodads array data
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, doodads_amount_adk * 56, DnG_map, current_dng_byte, doodads_data_length);
            current_dng_byte += doodads_data_length;
            current_adk_byte += doodads_data_length;

            //Add anchor doodads
            foreach (var harbour in Harbours_list)
            {
                if (harbour.anchorage)
                {
                    MemoryStream anchorDoodad = new();
                    using (BinaryWriter w = new(anchorDoodad))
                    {
                        w.Write([0x00, 0x1b, 0xff, 0xf1]);
                        w.Write([0x01, 0x00, 0x00, 0x00, 0x5B, 0x76, 0x5C, 0xEF, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00]);
                        w.Write(GenerateUniqueID());
                        w.Write([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x1D, 0x85, 0x47, 0x6F, 0x0F, 0x00, 0x00, 0x00]);

                        int anchor_x = (harbour.anchor_y % 2 == 0) ? harbour.anchor_x * 4 : (harbour.anchor_x * 4) + 2;
                        w.Write(anchor_x);
                        w.Write(harbour.anchor_y * 4);
                    }
                    byte[] anchorBytes = anchorDoodad.ToArray();
                    ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 0, anchorBytes);
                    current_adk_byte += anchorBytes.Length;
                }
            }

            //Overwrite lifetime doodads array length (template map has none)
            int lifetime_doodads_beginning = current_adk_byte;
            int lifetime_doodads_amount = BitConverter.ToInt32(DnG_map, current_dng_byte);
            current_dng_byte += 4;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 4, BitConverter.GetBytes(lifetime_doodads_amount));
            current_adk_byte += 4;
            //Write lifetime doodads array data
            int lifetime_doodads_data_length = lifetime_doodads_amount * 60;
            byte[] lifetime_doodads_bytes = new byte[lifetime_doodads_data_length];
            Buffer.BlockCopy(DnG_map, current_dng_byte, lifetime_doodads_bytes, 0, lifetime_doodads_data_length);

            byte[] maxValueBytes = BitConverter.GetBytes(int.MaxValue);
            for (int i = 0; i < lifetime_doodads_amount; i++)
            {
                // Overwrite offset 56..59 of each 60-byte element with int.MaxValue
                Buffer.BlockCopy(maxValueBytes, 0, lifetime_doodads_bytes, (i * 60) + 56, 4);
            }

            // remove_amount = 0 (template map has no existing lifetime doodad bytes to remove)
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 0, lifetime_doodads_bytes, 0, lifetime_doodads_data_length);

            current_dng_byte += lifetime_doodads_data_length;
            current_adk_byte += lifetime_doodads_data_length;

            //Overwrite blocking doodads array length
            int blocking_doodads_beginning = current_adk_byte;
            int blocking_doodads_amount = BitConverter.ToInt32(DnG_map, current_dng_byte);
            current_dng_byte += 4;
            int blocking_doodads_length_adk = BitConverter.ToInt32(adk_memory_stream.ToArray(), current_adk_byte);
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 4, BitConverter.GetBytes(blocking_doodads_amount));
            current_adk_byte += 4;
            //Overwrite blocking doodads array data
            int blocking_doodads_data_length = blocking_doodads_amount * 56;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, blocking_doodads_length_adk * 56, DnG_map, current_dng_byte, blocking_doodads_data_length);
            current_dng_byte += blocking_doodads_data_length;
            current_adk_byte += blocking_doodads_data_length;

            //Skip ambients header
            current_dng_byte += 16;
            current_adk_byte += 16;
            //Read the ambients array length (template map has no ambients)
            int ambients_beginning = current_adk_byte;

            int ambients_amount = BitConverter.ToInt32(DnG_map, current_dng_byte);
            current_dng_byte += 4;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 4, BitConverter.GetBytes(ambients_amount));
            current_adk_byte += 4;
            //Copy the ambients array data to the AdK map
            int ambientsDataLength = ambients_amount * 24;
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 0, DnG_map, current_dng_byte, ambientsDataLength);
            //End of the DnG map, no need to update current_dng_byte anymore
            current_adk_byte += ambientsDataLength;

            //Skip buoy connections header
            current_adk_byte += 36;
            //Generate buoy connections and harbour IDs
            GenerateBuoyConnections();
            //Write buoy connections amount (template map has none)
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 4, BitConverter.GetBytes(Buoy_connections.Count));
            current_adk_byte += 4;

            //Write buoy connections
            List<int[][]> route_paths = [];
            if (Harbours_list.Count > 0)
            {
                foreach (var (connection_id, harbour_source_id, harbour_target_id, buoy_source_x, buoy_source_y, buoy_target_x, buoy_target_y) in Buoy_connections)
                {
                    MemoryStream buoyStream = new();
                    using (BinaryWriter w = new(buoyStream))
                    {
                        //Write the first static value and the ID header
                        w.Write([0x01, 0x00, 0x00, 0x00, 0x2D, 0xD1, 0x27, 0x1C, 0x0E, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00]);
                        //Write the connection ID
                        w.Write(connection_id);
                        w.Write(0);
                        //Write the ID header
                        w.Write([0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00]);
                        //Write the source harbour ID
                        w.Write(harbour_source_id);
                        w.Write(0);
                        //Write the ID header
                        w.Write([0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00]);
                        //Write the target harbour ID
                        w.Write(harbour_target_id);
                        w.Write(0);
                        //Write the empty ship references list and the street ID (none)
                        w.Write([0x00, 0x00, 0x00, 0x00, 0x79, 0x3C, 0xF8, 0x25, 0x13, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
                    }

                    byte[] bBytes = buoyStream.ToArray();
                    ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 0, bBytes);
                    current_adk_byte += bBytes.Length;

                    //Compute the path connecting the buoys
                    int[][] buoyPath = FindPath(heightmap_logical, [buoy_source_x, buoy_source_y], [buoy_target_x, buoy_target_y], established_connections);

                    if (buoyPath != null)
                    {
                        route_paths.Add(buoyPath);
                        ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 0, BitConverter.GetBytes(buoyPath.Length));
                        current_adk_byte += 4;

                        foreach (int[] step in buoyPath)
                        {
                            MemoryStream stepMs = new();
                            using (BinaryWriter w = new(stepMs))
                            {
                                //PatternCursor
                                w.Write([0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00]);
                                //X
                                w.Write(step[0]);
                                //Y
                                w.Write(step[1]);
                            }
                            byte[] sBytes = stepMs.ToArray();
                            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 0, sBytes);
                            current_adk_byte += sBytes.Length;
                            established_connections.Add(step);
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
            ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 4, BitConverter.GetBytes(Harbours_list.Count));
            current_adk_byte += 4;

            //Write harbour data
            List<(int harbour, int buoy)> stored_buoys = [];
            for (int i = 0; i < Harbours_list.Count; i++)
            {
                var harbour = Harbours_list[i];
                int harbour_rotation = harbour.rotation;
                int[] connection_ids = [harbour.buoy_1_connection > 0 ? Harbour_data[i].buoy_1_connection_id : -1, harbour.buoy_2_connection > 0 ? Harbour_data[i].buoy_2_connection_id : -1];

                //Unconnected buoys are only stored when the buoy and its docks are in free water
                List<int> buoys = [];
                for (int b = 0; b < 2; b++)
                {
                    var (buoy_cell, dock_1, dock_2) = GetBuoyCells(harbour, b);
                    if (connection_ids[b] != -1 || (IsFreeWater(gridstates, buoy_cell.x, buoy_cell.y) && IsFreeWater(gridstates, dock_1.x, dock_1.y) && IsFreeWater(gridstates, dock_2.x, dock_2.y)))
                        buoys.Add(b);
                }

                MemoryStream harbourStream = new();
                using (BinaryWriter w = new(harbourStream))
                {
                    //Write harbour rotation
                    w.Write((byte[])HarbourRotations[harbour_rotation].Clone());

                    w.Write([0x02, 0x00, 0x00, 0x00, 0x0F, 0xA9, 0xE5, 0x3E, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00]);

                    //Write a harbour ID
                    w.Write(Harbour_data[i].harbour_id);

                    w.Write([0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00]);

                    //Write harbour flag stream_offset
                    w.Write(harbour.pos_x);
                    w.Write(harbour.pos_y);

                    w.Write(buoys.Count);

                    foreach (int b in buoys)
                    {
                        var (buoy, dock_1, dock_2) = GetBuoyCells(harbour, b);

                        w.Write([0x00, 0x00, 0x00, 0x00, 0x7F, 0x63, 0xCD, 0xE0, 0x13, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x20, 0x87, 0x07, 0xFF, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00]);

                        //Write docking position 1
                        w.Write(dock_1.x);
                        w.Write(dock_1.y);

                        w.Write([0x00, 0x00, 0x00, 0x00, 0x20, 0x87, 0x07, 0xFF, 0x15, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00]);

                        //Write docking position 2
                        w.Write(dock_2.x);
                        w.Write(dock_2.y);

                        w.Write([0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00]);

                        //Write buoy connection ID
                        if (connection_ids[b] == -1)
                            w.Write([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
                        else
                        {
                            w.Write(connection_ids[b]);
                            w.Write(0);
                        }

                        w.Write([0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00]);

                        //Write buoy position
                        w.Write(buoy.x);
                        w.Write(buoy.y);

                        stored_buoys.Add((i, b));
                    }

                    w.Write([0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF]);
                }

                byte[] harbourBytes = harbourStream.ToArray();
                ReplaceStreamBytes(adk_memory_stream, current_adk_byte, 0, harbourBytes);
                current_adk_byte += harbourBytes.Length;
            }

            // Skip caves data header
            current_adk_byte += 16;

            // Wipe template caves data (end of the file)
            adk_memory_stream.SetLength(current_adk_byte);
            adk_memory_stream.Position = current_adk_byte;

            int caves_beginning = current_adk_byte;
            int caves_amount = Caves_list.Count;

            // Pass leaveOpen: true so disposing the writer won't close adk_memory_stream
            using (BinaryWriter writer = new(adk_memory_stream, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                // Write the caves data to the AdK map
                writer.Write(caves_amount);
                foreach (var (pos_x, pos_y, type) in Caves_list)
                {
                    writer.Write(CaveTypes[type]);
                    writer.Write([0x00, 0x00, 0x00, 0x00, 0x74, 0x76, 0x80, 0x4A, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00]);
                    writer.Write(GenerateUniqueID());
                    // Pattern cursor
                    writer.Write([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00]);
                    // X
                    writer.Write(pos_x);
                    // Y
                    writer.Write(pos_y);
                }
                // Add empty 4 bytes at the end of the file
                writer.Write(0);
            }

            //Create arrays storing a map of occupied hexagons
            bool[,] logical_grid_blocking = new bool[map_size_x, map_size_y];
            bool[,] logical_grid_animals = new bool[map_size_x, map_size_y];
            bool[,] logical_grid_ambients = new bool[map_size_x, map_size_y];

            adk_byte_array = adk_memory_stream.ToArray();
            //Add 4 bytes to skip amounts
            if (Swap_list.Count > 0)
            {
                //Deposits
                for (int i = 0; i < deposits_amount; i++)
                {
                    int pos_x = BitConverter.ToInt32(adk_byte_array, deposits_beginning + 4 + (i * 108) + 48);
                    int pos_y = BitConverter.ToInt32(adk_byte_array, deposits_beginning + 4 + (i * 108) + 52);

                    logical_grid_blocking[pos_x, pos_y] = true;
                }
                //Animals
                for (int i = 0; i < animals_amount; i++)
                {
                    int pos_x = BitConverter.ToInt32(adk_byte_array, animals_beginning + 4 + (i * 244) + 52);
                    int pos_y = BitConverter.ToInt32(adk_byte_array, animals_beginning + 4 + (i * 244) + 56);

                    logical_grid_animals[pos_x, pos_y] = true;
                }
                //Blocking doodads
                for (int i = 0; i < blocking_doodads_amount; i++)
                {
                    //Removing the decimal component is an intended behaviour
                    int pos_x = BitConverter.ToInt32(adk_byte_array, blocking_doodads_beginning + 4 + (i * 56) + 48) / 4;
                    int pos_y = BitConverter.ToInt32(adk_byte_array, blocking_doodads_beginning + 4 + (i * 56) + 52) / 4;

                    logical_grid_blocking[pos_x, pos_y] = true;
                }
                //Ambients
                for (int i = 0; i < ambients_amount; i++)
                {
                    int pos_x = BitConverter.ToInt32(adk_byte_array, ambients_beginning + 4 + (i * 24) + 16);
                    int pos_y = BitConverter.ToInt32(adk_byte_array, ambients_beginning + 4 + (i * 24) + 20);

                    logical_grid_ambients[pos_x, pos_y] = true;
                }
                //Caves
                foreach (var (pos_x, pos_y, type) in Caves_list)
                {
                    logical_grid_blocking[pos_x, pos_y] = true;
                }
            }

            //Logical grid swapping
            foreach (var (tab, from, to) in Swap_list)
            {
                if (tab == 2)
                {
                    int source_type = DnG_logical_grid_types[from];
                    int target_type = AdK_logical_grid_types[to];

                    int source = BitConverter.ToInt32(DnG_logical_grid[from], 0);
                    byte[] target = AdK_logical_grid[to];

                    //Source and target types are equal
                    if (source_type == target_type || (source_type <= 1 && target_type <= 1))
                    {
                        //Deposits
                        if (source_type <= 1)
                        {
                            for (int i = 0; i < deposits_amount; i++)
                            {
                                //Skip deposits amount
                                int deposit_start = deposits_beginning + (i * 108) + 4;

                                // Read type buffer directly from stream
                                adk_memory_stream.Position = deposit_start;
                                byte[] type_buffer = new byte[4];
                                adk_memory_stream.Read(type_buffer, 0, 4);

                                if (source == BitConverter.ToInt32(type_buffer, 0))
                                {
                                    ReplaceStreamBytes(adk_memory_stream, deposit_start, 4, target, 0, 4);

                                    if (source_type != target_type)
                                    {
                                        // Read coordinates directly from stream at relative offsets
                                        adk_memory_stream.Position = deposit_start + 48;
                                        byte[] coord_buffer = new byte[8];
                                        adk_memory_stream.Read(coord_buffer, 0, 8);

                                        int pos_x = BitConverter.ToInt32(coord_buffer, 0);
                                        int pos_y = BitConverter.ToInt32(coord_buffer, 4);

                                        // Calculate grid state offset
                                        int target_byte = gridstates_beginning + (pos_x + pos_y * map_size_x) * 4;

                                        //Byte 1
                                        adk_memory_stream.Position = target_byte;
                                        int flag_int = adk_memory_stream.ReadByte();
                                        byte flag_byte = (byte)flag_int;

                                        if (target_type == 1)
                                        {
                                            flag_byte |= 0x01;  // Set is_blocked flag (0x01)
                                        }
                                        else
                                        {
                                            //Add a check for blocking textures underneath
                                            flag_byte &= 0xFE;  // Clear is_blocked flag (0x01)
                                        }

                                        adk_memory_stream.Position = target_byte;
                                        adk_memory_stream.WriteByte(flag_byte);
                                    }
                                }
                            }
                            continue;
                        }

                        //Animals
                        if (source_type == 2)
                        {
                            for (int i = 0; i < animals_amount; i++)
                            {
                                //Skip animals amount
                                int animal_start = animals_beginning + (i * 244) + 4;

                                // Read type buffer directly from stream
                                adk_memory_stream.Position = animal_start;
                                byte[] type_buffer = new byte[4];
                                adk_memory_stream.Read(type_buffer, 0, 4);

                                if (source == BitConverter.ToInt32(type_buffer, 0))
                                {
                                    ReplaceStreamBytes(adk_memory_stream, animal_start, 4, target, 0, 4);
                                }
                            }
                            continue;
                        }

                        //Blocking doodads
                        if (source_type == 3)
                        {
                            for (int i = 0; i < blocking_doodads_amount; i++)
                            {
                                //Skip doodads amount
                                int blocking_doodad_start = blocking_doodads_beginning + (i * 56) + 4;

                                // Read type buffer directly from stream
                                adk_memory_stream.Position = blocking_doodad_start;
                                byte[] type_buffer = new byte[4];
                                adk_memory_stream.Read(type_buffer, 0, 4);

                                if (source == BitConverter.ToInt32(type_buffer, 0))
                                {
                                    ReplaceStreamBytes(adk_memory_stream, blocking_doodad_start, 4, target, 0, 4);
                                }
                            }
                            continue;
                        }

                        //Ambients
                        if (source_type == 4)
                        {
                            for (int i = 0; i < ambients_amount; i++)
                            {
                                //Skip ambients amount
                                int ambient_start = ambients_beginning + (i * 24) + 4;

                                // Read type buffer directly from stream
                                adk_memory_stream.Position = ambient_start;
                                byte[] type_buffer = new byte[4];
                                adk_memory_stream.Read(type_buffer, 0, 4);

                                if (source == BitConverter.ToInt32(type_buffer, 0))
                                {
                                    ReplaceStreamBytes(adk_memory_stream, ambient_start, 4, target, 0, 4);
                                }
                            }
                            continue;
                        }

                    }
                    //Source and target types are not equal
                    else
                    {
                        //Extraction
                        List<(int pos_x, int pos_y, byte[] ID)> extracted_objects = [];

                        //Deposits
                        if (source_type <= 1)
                        {
                            for (int i = deposits_amount - 1; i >= 0; i--)
                            {
                                //Skip deposits amount
                                int deposit_start = deposits_beginning + (i * 108) + 4;

                                // Read type buffer directly from stream
                                adk_memory_stream.Position = deposit_start;
                                byte[] type_buffer = new byte[4];
                                adk_memory_stream.Read(type_buffer, 0, 4);

                                if (source == BitConverter.ToInt32(type_buffer, 0))
                                {
                                    // Read coordinates directly from stream at relative offsets
                                    adk_memory_stream.Position = deposit_start + 48;
                                    byte[] temp_buffer = new byte[8];
                                    adk_memory_stream.Read(temp_buffer, 0, 8);

                                    int pos_x = BitConverter.ToInt32(temp_buffer, 0);
                                    int pos_y = BitConverter.ToInt32(temp_buffer, 4);

                                    // Calculate grid state offset
                                    int target_byte = gridstates_beginning + (pos_x + pos_y * map_size_x) * 4;

                                    //Byte 1
                                    adk_memory_stream.Position = target_byte;
                                    int flag_int = adk_memory_stream.ReadByte();
                                    byte flag_byte = (byte)flag_int;

                                    flag_byte &= 0x7F; //Clear has_deposit flag (0x80)
                                    //Add a check for blocking textures underneath
                                    if (source_type == 1)
                                    {
                                        flag_byte &= 0xFE;  // Clear is_blocked flag (0x01)
                                    }

                                    adk_memory_stream.Position = target_byte;
                                    adk_memory_stream.WriteByte(flag_byte);

                                    //Read ID
                                    adk_memory_stream.Position = deposit_start + 28;
                                    adk_memory_stream.Read(temp_buffer, 0, 8);

                                    //Remove the deposit (Insert nothing)
                                    ReplaceStreamBytes(adk_memory_stream, deposit_start, 108, temp_buffer, 0, 0);
                                    extracted_objects.Add((pos_x, pos_y, (byte[])temp_buffer.Clone()));
                                    logical_grid_blocking[pos_x, pos_y] = false;
                                    deposits_amount--;

                                    //Shift other arrays
                                    animals_beginning -= 108;
                                    doodads_beginning -= 108;
                                    lifetime_doodads_beginning -= 108;
                                    blocking_doodads_beginning -= 108;
                                    ambients_beginning -= 108;
                                    caves_beginning -= 108;
                                }
                            }
                        }
                        else
                        {
                            switch (source_type)
                            {
                                //Animals
                                case 2:
                                    {
                                        for (int i = animals_amount - 1; i >= 0; i--)
                                        {
                                            //Skip animals amount
                                            int animal_start = animals_beginning + (i * 244) + 4;

                                            // Read type buffer directly from stream
                                            adk_memory_stream.Position = animal_start;
                                            byte[] type_buffer = new byte[4];
                                            adk_memory_stream.Read(type_buffer, 0, 4);

                                            if (source == BitConverter.ToInt32(type_buffer, 0))
                                            {
                                                // Read coordinates directly from stream at relative offsets
                                                adk_memory_stream.Position = animal_start + 52;
                                                byte[] temp_buffer = new byte[8];
                                                adk_memory_stream.Read(temp_buffer, 0, 8);

                                                int pos_x = BitConverter.ToInt32(temp_buffer, 0);
                                                int pos_y = BitConverter.ToInt32(temp_buffer, 4);

                                                //Read ID
                                                adk_memory_stream.Position = animal_start + 28;
                                                adk_memory_stream.Read(temp_buffer, 0, 8);

                                                //Remove the animal (Insert nothing)
                                                ReplaceStreamBytes(adk_memory_stream, animal_start, 244, temp_buffer, 0, 0);
                                                extracted_objects.Add((pos_x, pos_y, (byte[])temp_buffer.Clone()));
                                                logical_grid_animals[pos_x, pos_y] = false;
                                                animals_amount--;

                                                //Shift other arrays
                                                doodads_beginning -= 244;
                                                lifetime_doodads_beginning -= 244;
                                                blocking_doodads_beginning -= 244;
                                                ambients_beginning -= 244;
                                                caves_beginning -= 244;
                                            }
                                        }
                                        break;
                                    }
                                //Blocking doodads
                                case 3:
                                    {
                                        for (int i = blocking_doodads_amount - 1; i >= 0; i--)
                                        {
                                            //Skip blocking doodads amount
                                            int blocking_doodad_start = blocking_doodads_beginning + (i * 56) + 4;

                                            // Read type buffer directly from stream
                                            adk_memory_stream.Position = blocking_doodad_start;
                                            byte[] type_buffer = new byte[4];
                                            adk_memory_stream.Read(type_buffer, 0, 4);

                                            if (source == BitConverter.ToInt32(type_buffer, 0))
                                            {
                                                // Read coordinates directly from stream at relative offsets
                                                adk_memory_stream.Position = blocking_doodad_start + 48;
                                                byte[] temp_buffer = new byte[8];
                                                adk_memory_stream.Read(temp_buffer, 0, 8);

                                                int pos_x = BitConverter.ToInt32(temp_buffer, 0) / 4;
                                                int pos_y = BitConverter.ToInt32(temp_buffer, 4) / 4;

                                                // Calculate grid state offset
                                                int target_byte = gridstates_beginning + (pos_x + pos_y * map_size_x) * 4 + 1;

                                                //Byte 2
                                                adk_memory_stream.Position = target_byte;
                                                int flag_int = adk_memory_stream.ReadByte();
                                                byte flag_byte = (byte)flag_int;

                                                flag_byte &= 0xFB; //Clear is_large_doodad flag (0x04)

                                                adk_memory_stream.Position = target_byte;
                                                adk_memory_stream.WriteByte(flag_byte);

                                                //Read ID
                                                adk_memory_stream.Position = blocking_doodad_start + 28;
                                                adk_memory_stream.Read(temp_buffer, 0, 8);

                                                //Remove the blocking doodad (Insert nothing)
                                                ReplaceStreamBytes(adk_memory_stream, blocking_doodad_start, 56, temp_buffer, 0, 0);
                                                extracted_objects.Add((pos_x, pos_y, (byte[])temp_buffer.Clone()));
                                                logical_grid_blocking[pos_x, pos_y] = false;
                                                blocking_doodads_amount--;

                                                //Shift other arrays
                                                ambients_beginning -= 56;
                                                caves_beginning -= 56;
                                            }
                                        }
                                        break;
                                    }
                                //Ambients
                                case 4:
                                    {
                                        for (int i = ambients_amount - 1; i >= 0; i--)
                                        {
                                            //Skip ambients amount
                                            int ambient_start = ambients_beginning + (i * 24) + 4;

                                            // Read type buffer directly from stream
                                            adk_memory_stream.Position = ambient_start;
                                            byte[] type_buffer = new byte[4];
                                            adk_memory_stream.Read(type_buffer, 0, 4);

                                            if (source == BitConverter.ToInt32(type_buffer, 0))
                                            {
                                                // Read coordinates directly from stream at relative offsets
                                                adk_memory_stream.Position = ambient_start + 16;
                                                byte[] temp_buffer = new byte[8];
                                                adk_memory_stream.Read(temp_buffer, 0, 8);

                                                int pos_x = BitConverter.ToInt32(temp_buffer, 0);
                                                int pos_y = BitConverter.ToInt32(temp_buffer, 4);

                                                //Generate a unique ID (ambients don't need one)
                                                Array.Copy(BitConverter.GetBytes(GenerateUniqueID()), temp_buffer, 4);
                                                Array.Clear(temp_buffer, 4, 4);

                                                //Remove the ambient (Insert nothing)
                                                ReplaceStreamBytes(adk_memory_stream, ambient_start, 24, temp_buffer, 0, 0);
                                                extracted_objects.Add((pos_x, pos_y, (byte[])temp_buffer.Clone()));
                                                logical_grid_ambients[pos_x, pos_y] = false;
                                                ambients_amount--;

                                                //Shift other arrays
                                                caves_beginning -= 24;
                                            }
                                        }
                                        break;
                                    }
                            }
                        }

                        //Writing

                        //Deposits
                        if (target_type <= 1)
                        {
                            foreach (var (pos_x, pos_y, ID) in extracted_objects)
                            {
                                if (!logical_grid_blocking[pos_x, pos_y])
                                {
                                    MemoryStream deposit_stream = new();
                                    using (BinaryWriter w = new(deposit_stream))
                                    {
                                        w.Write(target);
                                        w.Write([
    0x01, 0x00, 0x00, 0x00, 0x39, 0x9D, 0xDB, 0x95,
    0x07, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00
]);
                                        //ID
                                        w.Write(ID);
                                        w.Write([
    0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54,
    0x0D, 0x00, 0x00, 0x00
]);
                                        //Logical X
                                        w.Write(pos_x);
                                        //Logical Y
                                        w.Write(pos_y);
                                        w.Write([
    0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5,
    0x0E, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00,
    0x1D, 0x85, 0x47, 0x6F, 0x0F, 0x00, 0x00, 0x00
]);
                                        //Detailed X
                                        if (pos_y % 2 == 0)
                                        {
                                            w.Write(pos_x * 4);
                                        }
                                        else
                                        {
                                            w.Write(pos_x * 4 + 2);
                                        }
                                        //Detailed Y
                                        w.Write(pos_y * 4);
                                        w.Write([0x00, 0x00, 0x80, 0x3F, 0x00, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF]);
                                    }

                                    // Calculate grid state offset
                                    int target_byte = gridstates_beginning + (pos_x + pos_y * map_size_x) * 4;

                                    //Byte 1
                                    adk_memory_stream.Position = target_byte;
                                    int flag_int = adk_memory_stream.ReadByte();
                                    byte flag_byte = (byte)flag_int;

                                    flag_byte |= 0x80; //Set has_deposit flag (0x80)
                                    if (target_type == 1)
                                    {
                                        flag_byte |= 0x01;  // Set is_blocked flag (0x01)
                                    }

                                    adk_memory_stream.Position = target_byte;
                                    adk_memory_stream.WriteByte(flag_byte);

                                    ReplaceStreamBytes(adk_memory_stream, deposits_beginning + 4, 0, deposit_stream.ToArray(), 0, -1);
                                    deposits_amount++;

                                    logical_grid_blocking[pos_x, pos_y] = true;

                                    //Shift other arrays
                                    animals_beginning += 108;
                                    doodads_beginning += 108;
                                    lifetime_doodads_beginning += 108;
                                    blocking_doodads_beginning += 108;
                                    ambients_beginning += 108;
                                    caves_beginning += 108;
                                }
                            }
                        }
                        else
                        {
                            switch (target_type)
                            {
                                //Animals
                                case 2:
                                    {
                                        foreach (var (pos_x, pos_y, ID) in extracted_objects)
                                        {
                                            if (!logical_grid_animals[pos_x, pos_y])
                                            {
                                                MemoryStream animal_stream = new();
                                                using (BinaryWriter w = new(animal_stream))
                                                {
                                                    w.Write(target);
                                                    w.Write([
    0x02, 0x00, 0x00, 0x00, 0xE4, 0x8A, 0x52, 0x6A,
    0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00
]);
                                                    w.Write(ID);
                                                    w.Write([
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00
]);
                                                    //X
                                                    w.Write(pos_x);
                                                    //Y
                                                    w.Write(pos_y);
                                                    w.Write([
    0x01, 0x00, 0x00, 0x00, 0x77, 0x67, 0x5B, 0x0D,
    0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x93, 0xE4, 0x70, 0x1B, 0x0E, 0x00, 0x00, 0x00,
    0x01, 0x00, 0x00, 0x00, 0x1B, 0x07, 0xBA, 0x9C,
    0x12, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54,
    0x0D, 0x00, 0x00, 0x00
]);
                                                    //X
                                                    w.Write(pos_x);
                                                    //Y
                                                    w.Write(pos_y);
                                                    w.Write([
    0x00, 0x00, 0x00, 0x00, 0xAE, 0x02, 0x54, 0x70,
    0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00
]);
                                                    //X
                                                    w.Write(pos_x);
                                                    //Y
                                                    w.Write(pos_y);
                                                    w.Write([0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00]);
                                                    //X
                                                    w.Write(pos_x);
                                                    //Y
                                                    w.Write(pos_y);
                                                    w.Write([
    0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54,
    0x0D, 0x00, 0x00, 0x00, 0xFF, 0xFF, 0xFF, 0xFF,
    0xFF, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x00,
    0xF6, 0x8C, 0x70, 0x08, 0x15, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x80, 0xBF, 0x00, 0x00, 0x00, 0x00,
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0x6A, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
    0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00,
    0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF
]);
                                                }

                                                ReplaceStreamBytes(adk_memory_stream, animals_beginning + 4, 0, animal_stream.ToArray(), 0, -1);
                                                animals_amount++;

                                                logical_grid_animals[pos_x, pos_y] = true;

                                                //Shift other arrays
                                                doodads_beginning += 244;
                                                lifetime_doodads_beginning += 244;
                                                blocking_doodads_beginning += 244;
                                                ambients_beginning += 244;
                                                caves_beginning += 244;
                                            }
                                        }
                                        break;
                                    }
                                //Blocking doodads
                                case 3:
                                    {
                                        foreach (var (pos_x, pos_y, ID) in extracted_objects)
                                        {
                                            if (!logical_grid_blocking[pos_x, pos_y])
                                            {
                                                MemoryStream blocking_doodad_stream = new();
                                                using (BinaryWriter w = new(blocking_doodad_stream))
                                                {
                                                    w.Write(target);
                                                    w.Write([0x01, 0x00, 0x00, 0x00, 0x5B, 0x76, 0x5C, 0xEF, 0x0D, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00]);
                                                    w.Write(ID);
                                                    w.Write([0x00, 0x00, 0x00, 0x00, 0x1D, 0x85, 0x47, 0x6F, 0x0F, 0x00, 0x00, 0x00]);
                                                    //Detailed X
                                                    if (pos_y % 2 == 0)
                                                    {
                                                        w.Write(pos_x * 4);
                                                    }
                                                    else
                                                    {
                                                        w.Write(pos_x * 4 + 2);
                                                    }
                                                    //Detailed Y
                                                    w.Write(pos_y * 4);
                                                }

                                                // Calculate grid state offset
                                                int target_byte = gridstates_beginning + (pos_x + pos_y * map_size_x) * 4 + 1;

                                                //Byte 2
                                                adk_memory_stream.Position = target_byte;
                                                int flag_int = adk_memory_stream.ReadByte();
                                                byte flag_byte = (byte)flag_int;

                                                flag_byte |= 0x04; //Set is_large_doodad flag (0x04)

                                                adk_memory_stream.Position = target_byte;
                                                adk_memory_stream.WriteByte(flag_byte);

                                                ReplaceStreamBytes(adk_memory_stream, blocking_doodads_beginning + 4, 0, blocking_doodad_stream.ToArray(), 0, -1);
                                                blocking_doodads_amount++;

                                                logical_grid_blocking[pos_x, pos_y] = true;

                                                //Shift other arrays
                                                ambients_beginning += 56;
                                                caves_beginning += 56;
                                            }
                                        }
                                        break;
                                    }
                                //Ambients
                                case 4:
                                    {
                                        foreach (var (pos_x, pos_y, ID) in extracted_objects)
                                        {
                                            if (!logical_grid_ambients[pos_x, pos_y])
                                            {
                                                MemoryStream ambient_stream = new();
                                                using (BinaryWriter w = new(ambient_stream))
                                                {
                                                    w.Write(target);
                                                    w.Write([0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00]);
                                                    //X
                                                    w.Write(pos_x);
                                                    //Y
                                                    w.Write(pos_y);
                                                }

                                                ReplaceStreamBytes(adk_memory_stream, ambients_beginning + 4, 0, ambient_stream.ToArray(), 0, -1);
                                                ambients_amount++;

                                                logical_grid_ambients[pos_x, pos_y] = true;

                                                //Shift other arrays
                                                caves_beginning += 24;
                                            }
                                        }
                                        break;
                                    }
                                //Caves
                                case 5:
                                    {
                                        foreach (var (pos_x, pos_y, ID) in extracted_objects)
                                        {
                                            if (!logical_grid_blocking[pos_x, pos_y])
                                            {
                                                MemoryStream cave_stream = new();
                                                using (BinaryWriter w = new(cave_stream))
                                                {
                                                    w.Write(target);
                                                    w.Write([0x00, 0x00, 0x00, 0x00, 0x74, 0x76, 0x80, 0x4A, 0x0B, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xDD, 0x2D, 0xFD, 0xC5, 0x0E, 0x00, 0x00, 0x00]);
                                                    w.Write(ID);
                                                    // Pattern cursor
                                                    w.Write([0x00, 0x00, 0x00, 0x00, 0xA2, 0xFE, 0x49, 0x54, 0x0D, 0x00, 0x00, 0x00]);
                                                    // X
                                                    w.Write(pos_x);
                                                    // Y
                                                    w.Write(pos_y);
                                                }

                                                // Calculate grid state offset
                                                int target_byte = gridstates_beginning + (pos_x + pos_y * map_size_x) * 4 + 1;

                                                //Byte 2
                                                adk_memory_stream.Position = target_byte;
                                                int flag_int = adk_memory_stream.ReadByte();
                                                byte flag_byte = (byte)flag_int;

                                                flag_byte |= 0x04; //Set is_large_doodad flag (0x04)

                                                adk_memory_stream.Position = target_byte;
                                                adk_memory_stream.WriteByte(flag_byte);

                                                ReplaceStreamBytes(adk_memory_stream, caves_beginning + 4, 0, cave_stream.ToArray(), 0, -1);
                                                caves_amount++;

                                                logical_grid_blocking[pos_x, pos_y] = true;
                                            }
                                        }
                                        break;
                                    }
                            }
                        }
                    }
                }
            }

            //Update logical grid object amounts
            if (Swap_list.Count > 0)
            {
                //Deposits
                ReplaceStreamBytes(adk_memory_stream, deposits_beginning, 4, BitConverter.GetBytes(deposits_amount), 0, 4);
                //Animals
                ReplaceStreamBytes(adk_memory_stream, animals_beginning, 4, BitConverter.GetBytes(animals_amount), 0, 4);
                //Blocking doodads
                ReplaceStreamBytes(adk_memory_stream, blocking_doodads_beginning, 4, BitConverter.GetBytes(blocking_doodads_amount), 0, 4);
                //Ambients
                ReplaceStreamBytes(adk_memory_stream, ambients_beginning, 4, BitConverter.GetBytes(ambients_amount), 0, 4);
                //Caves
                ReplaceStreamBytes(adk_memory_stream, caves_beginning, 4, BitConverter.GetBytes(caves_amount), 0, 4);
            }

            // Doodads grid swapping
            foreach (var (tab, from, to) in Swap_list)
            {
                if (tab != 3) continue;

                bool has_source_lifetime = is_lifetime_dng[from] != 0;
                bool has_target_lifetime = is_lifetime_adk[to] != 0;
                int source_type = BitConverter.ToInt32(doodads_dng[from], 0);
                byte[] target_type = doodads_adk[to];

                // 1. Same array type: In-place ID overwrite for ALL matching instances
                if (has_source_lifetime == has_target_lifetime)
                {
                    int start = has_source_lifetime ? lifetime_doodads_beginning : doodads_beginning;
                    int count = has_source_lifetime ? lifetime_doodads_amount : doodads_amount;
                    int stride = has_source_lifetime ? 60 : 56;

                    for (int i = 0; i < count; i++)
                    {
                        int pos = start + 4 + (i * stride);
                        adk_memory_stream.Position = pos;

                        byte[] type_buffer = new byte[4];
                        adk_memory_stream.Read(type_buffer, 0, 4);

                        if (BitConverter.ToInt32(type_buffer, 0) == source_type)
                        {
                            ReplaceStreamBytes(adk_memory_stream, pos, 4, target_type, 0, 4);
                        }
                    }
                    continue;
                }

                // 2. Cross-array move: Iterate BACKWARD to preserve offsets of unexamined elements
                int srcStride = has_source_lifetime ? 60 : 56;
                int initialSrcCount = has_source_lifetime ? lifetime_doodads_amount : doodads_amount;

                for (int i = initialSrcCount - 1; i >= 0; i--)
                {
                    int srcStart = has_source_lifetime ? lifetime_doodads_beginning : doodads_beginning;
                    int pos = srcStart + 4 + (i * srcStride);

                    adk_memory_stream.Position = pos;
                    byte[] type_buffer = new byte[4];
                    adk_memory_stream.Read(type_buffer, 0, 4);

                    if (BitConverter.ToInt32(type_buffer, 0) != source_type) continue;

                    // Extract 52-byte payload
                    byte[] payload = new byte[52];
                    adk_memory_stream.Read(payload, 0, 52);

                    // Remove source entry from stream
                    ReplaceStreamBytes(adk_memory_stream, pos, srcStride, [], 0, 0);

                    // Update source count and offset headers
                    if (has_source_lifetime)
                    {
                        lifetime_doodads_amount--;
                        ReplaceStreamBytes(adk_memory_stream, lifetime_doodads_beginning, 4, BitConverter.GetBytes(lifetime_doodads_amount), 0, 4);
                    }
                    else
                    {
                        doodads_amount--;
                        ReplaceStreamBytes(adk_memory_stream, doodads_beginning, 4, BitConverter.GetBytes(doodads_amount), 0, 4);

                        // Standard section shrunk by 56 bytes; pull lifetime start back
                        lifetime_doodads_beginning -= 56;
                        ReplaceStreamBytes(adk_memory_stream, lifetime_doodads_beginning, 4, BitConverter.GetBytes(lifetime_doodads_amount), 0, 4);
                    }

                    // Assemble new target byte payload (Standard = 56B, Lifetime = 60B)
                    byte[] newEntry = new byte[has_target_lifetime ? 60 : 56];
                    Buffer.BlockCopy(target_type, 0, newEntry, 0, 4);
                    Buffer.BlockCopy(payload, 0, newEntry, 4, 52);
                    if (has_target_lifetime)
                    {
                        Buffer.BlockCopy(BitConverter.GetBytes(int.MaxValue), 0, newEntry, 56, 4);
                    }

                    // Insert into destination section & sync stream offsets
                    if (has_target_lifetime)
                    {
                        int dstPos = lifetime_doodads_beginning + 4 + (lifetime_doodads_amount * 60);
                        ReplaceStreamBytes(adk_memory_stream, dstPos, 0, newEntry, 0, 60);

                        lifetime_doodads_amount++;
                        ReplaceStreamBytes(adk_memory_stream, lifetime_doodads_beginning, 4, BitConverter.GetBytes(lifetime_doodads_amount), 0, 4);
                    }
                    else
                    {
                        int dstPos = doodads_beginning + 4 + (doodads_amount * 56);
                        ReplaceStreamBytes(adk_memory_stream, dstPos, 0, newEntry, 0, 56);

                        doodads_amount++;
                        ReplaceStreamBytes(adk_memory_stream, doodads_beginning, 4, BitConverter.GetBytes(doodads_amount), 0, 4);

                        // Standard section expanded by 56 bytes; push lifetime start forward
                        lifetime_doodads_beginning += 56;
                        ReplaceStreamBytes(adk_memory_stream, lifetime_doodads_beginning, 4, BitConverter.GetBytes(lifetime_doodads_amount), 0, 4);
                    }
                }
            }

            //Update doodads grid amounts
            if (Swap_list.Count > 0)
            {
                //Standard
                ReplaceStreamBytes(adk_memory_stream, doodads_beginning, 4, BitConverter.GetBytes(doodads_amount), 0, 4);
                //Lifetime
                ReplaceStreamBytes(adk_memory_stream, lifetime_doodads_beginning, 4, BitConverter.GetBytes(lifetime_doodads_amount), 0, 4);
            }

            //Remove water sign lifetime doodad with no texture
            if (lifetime_doodads_amount > 0)
            {
                int water_sign = BitConverter.ToInt32([0x43, 0xA3, 0x1A, 0x12], 0);
                byte[] empty_buffer = [];

                // Iterate backward to prevent stream index shifts from affecting remaining checks
                for (int i = lifetime_doodads_amount - 1; i >= 0; i--)
                {
                    //60 bytes per lifetime doodad + lifetime doodads amount
                    int target_byte_offset = lifetime_doodads_beginning + 4 + (i * 60);

                    // Read directly from the current stream position
                    adk_memory_stream.Position = target_byte_offset;
                    byte[] type_buffer = new byte[4];
                    adk_memory_stream.Read(type_buffer, 0, 4);

                    if (water_sign == BitConverter.ToInt32(type_buffer, 0))
                    {
                        ReplaceStreamBytes(adk_memory_stream, target_byte_offset, 60, empty_buffer, 0, 0);
                        lifetime_doodads_amount--;
                    }
                }

                //Update lifetime doodads amount
                ReplaceStreamBytes(adk_memory_stream, lifetime_doodads_beginning, 4, BitConverter.GetBytes(lifetime_doodads_amount), 0, 4);
            }

            //The game loads the grid states and continents as stored, so they have to match everything written above
            byte[] final_map = adk_memory_stream.ToArray();
            gridstates = S2mRules.ReadUInts(final_map, gridstates_beginning, map_area);

            int[] continent_ids = S2mRules.ComputeContinents(gridstates, S2mRules.ReadUInts(final_map, textures_beginning, map_area), map_size_x, map_size_y, out var continents);

            List<string> harbour_errors = ValidateHarbours(gridstates, continent_ids, continents, stored_buoys, route_paths);
            if (harbour_errors.Count > 0)
            {
                MessageBox.Show(string.Join(Environment.NewLine, harbour_errors.Take(15)), "Invalid harbour placement", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return null;
            }

            foreach (var harbour in Harbours_list)
            {
                gridstates[harbour.pos_y * map_size_x + harbour.pos_x] |= S2mRules.Harbour;
            }
            foreach (var (harbour, buoy) in stored_buoys)
            {
                var (buoy_cell, dock_1, dock_2) = GetBuoyCells(Harbours_list[harbour], buoy);
                gridstates[buoy_cell.y * map_size_x + buoy_cell.x] |= S2mRules.HarbourExit;
                gridstates[dock_1.y * map_size_x + dock_1.x] |= S2mRules.Dock;
                gridstates[dock_2.y * map_size_x + dock_2.x] |= S2mRules.Dock;
            }
            foreach (int[][] path in route_paths)
            {
                foreach (int[] step in path)
                    gridstates[step[1] * map_size_x + step[0]] |= S2mRules.ShipRoute;
            }
            S2mRules.WriteUInts(gridstates, final_map, gridstates_beginning);

            Buffer.BlockCopy(BitConverter.GetBytes(next_unique_id), 0, final_map, unique_id_counter_offset, 8);

            byte[] continents_section = S2mRules.SerializeContinents(continent_ids, continents, map_size_x, map_size_y);
            return [.. final_map[..continents_beginning], .. continents_section, .. final_map[(continents_beginning + continents_length)..]];
        }

        //Buoy (harbour exit) and its two docking positions
        private static ((int x, int y) buoy, (int x, int y) dock_1, (int x, int y) dock_2) GetBuoyCells(
            (int pos_x, int pos_y, int rotation, bool anchorage, int anchor_x, int anchor_y, int buoy_1_connection, int buoy_2_connection) harbour,
            int buoySubIndex)
        {
            var docks = buoySubIndex == 0 ? buoy1_docking_positions : buoy2_docking_positions;
            var d1 = docks[harbour.rotation, 0];
            var d2 = docks[harbour.rotation, 1];
            return (GetBuoyWorldCoordinates(harbour, buoySubIndex),
                (harbour.pos_x + d1.offsetX, harbour.pos_y + d1.offsetY),
                (harbour.pos_x + d2.offsetX, harbour.pos_y + d2.offsetY));
        }

        private bool IsFreeWater(uint[] gridstates, int x, int y) =>
            x >= 0 && x < map_size_x && y >= 0 && y < map_size_y &&
            (gridstates[y * map_size_x + x] & (S2mRules.Water | S2mRules.LogicObject)) == S2mRules.Water;

        //Placement rules the game checks when harbours are built in its own editor
        private List<string> ValidateHarbours(uint[] gridstates, int[] continent_ids, List<S2mRules.Continent> continents, List<(int harbour, int buoy)> stored_buoys, List<int[][]> route_paths)
        {
            List<string> errors = [];
            const uint harbour_blockers = S2mRules.Blocked | S2mRules.Water | 0x20 | S2mRules.Cliff | S2mRules.Deposit | 0x100 | S2mRules.LogicObject;

            for (int i = 0; i < Harbours_list.Count; i++)
            {
                var (pos_x, pos_y, _, _, _, _, _, _) = Harbours_list[i];
                int cell = pos_y * map_size_x + pos_x;

                if ((gridstates[cell] & harbour_blockers) != 0)
                    errors.Add($"Harbour #{i + 1} is not on free land.");
                else if (continent_ids[cell] < 0 || continents[continent_ids[cell]].IsWater)
                    errors.Add($"Harbour #{i + 1} is not on walkable land.");

                for (int j = i + 1; j < Harbours_list.Count; j++)
                {
                    if (S2mRules.HexDistance(pos_x, pos_y, Harbours_list[j].pos_x, Harbours_list[j].pos_y) <= 1)
                        errors.Add($"Harbours #{i + 1} and #{j + 1} are next to each other.");
                }
            }

            foreach (var (harbour, buoy) in stored_buoys)
            {
                var (buoy_cell, dock_1, dock_2) = GetBuoyCells(Harbours_list[harbour], buoy);
                if (!IsFreeWater(gridstates, buoy_cell.x, buoy_cell.y) || !IsFreeWater(gridstates, dock_1.x, dock_1.y) || !IsFreeWater(gridstates, dock_2.x, dock_2.y))
                {
                    errors.Add($"Harbour #{harbour + 1} buoy {buoy + 1} or one of its docking positions is not in open water.");
                    continue;
                }

                int water_body = continent_ids[buoy_cell.y * map_size_x + buoy_cell.x];
                if (continent_ids[dock_1.y * map_size_x + dock_1.x] != water_body || continent_ids[dock_2.y * map_size_x + dock_2.x] != water_body)
                    errors.Add($"Harbour #{harbour + 1} buoy {buoy + 1} and its docking positions are in different water bodies.");
            }

            foreach (int[][] path in route_paths)
            {
                int[] start = path[0], end = path[^1];
                if (S2mRules.HexDistance(start[0], start[1], end[0], end[1]) <= 2)
                    errors.Add($"The buoys at ({start[0]}, {start[1]}) and ({end[0]}, {end[1]}) are too close to be connected.");

                int water_body = continent_ids[start[1] * map_size_x + start[0]];
                if (path.Any(step => !IsFreeWater(gridstates, step[0], step[1]) || continent_ids[step[1] * map_size_x + step[0]] != water_body))
                    errors.Add($"The connection from ({start[0]}, {start[1]}) to ({end[0]}, {end[1]}) crosses something other than open water.");
            }

            return errors;
        }

        // MemoryStream Byte Replacement Helper Method
        private static void ReplaceStreamBytes(MemoryStream stream, int stream_offset, int remove_amount, byte[] insert_bytes, int insert_bytes_offset = 0, int insert_amount = -1)
        {
            if (insert_amount < 0) insert_amount = insert_bytes.Length;

            if (remove_amount == insert_amount)
            {
                // Direct overwrite - Fast O(1)
                stream.Position = stream_offset;
                stream.Write(insert_bytes, insert_bytes_offset, insert_amount);
            }
            else
            {
                // Resizing Stream - Slice existing content and splice new data
                byte[] buffer = stream.ToArray();
                stream.SetLength(0);
                stream.Write(buffer, 0, stream_offset);
                stream.Write(insert_bytes, insert_bytes_offset, insert_amount);

                int remainingOffset = stream_offset + remove_amount;
                if (remainingOffset < buffer.Length)
                {
                    stream.Write(buffer, remainingOffset, buffer.Length - remainingOffset);
                }
            }
        }

        //The map's ID counter must stay above every stored ID: the game takes IDs for new objects from it
        long next_unique_id;

        private int GenerateUniqueID() => checked((int)next_unique_id++);

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
        public List<(int connection_id, int harbour_source_id, int harbour_target_id, int buoy_source_x, int buoy_source_y, int buoy_target_x, int buoy_target_y)> Buoy_connections
            = [];

        public List<(int harbour_id, int buoy_1_connection_id, int buoy_2_connection_id)> Harbour_data
            = [];

        public void GenerateBuoyConnections()
        {
            Buoy_connections.Clear();
            Harbour_data.Clear();
            established_connections.Clear();

            var processedPairs = new HashSet<(int, int)>();

            // 1. Generate unique random IDs for all harbours upfront
            int[] harbourIds = new int[Harbours_list.Count];
            for (int i = 0; i < Harbours_list.Count; i++)
            {
                harbourIds[i] = GenerateUniqueID();
            }

            // 2D array to track connection IDs per harbour buoy [harborIndex, buoySubIndex]
            int[,] harbourBuoyConnectionIds = new int[Harbours_list.Count, 2];

            for (int i = 0; i < Harbours_list.Count; i++)
            {
                var (_, _, _, _, _, _, buoy_1_connection, buoy_2_connection) = Harbours_list[i];

                // Process Buoy 1 (Sub-index 0)
                ProcessConnection(i, 0, buoy_1_connection, processedPairs, harbourBuoyConnectionIds, harbourIds);

                // Process Buoy 2 (Sub-index 1)
                ProcessConnection(i, 1, buoy_2_connection, processedPairs, harbourBuoyConnectionIds, harbourIds);
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
        public static (int x, int y) GetBuoyWorldCoordinates(
            (int pos_x, int pos_y, int rotation, bool anchorage, int anchor_x, int anchor_y, int buoy_1_connection, int buoy_2_connection) harbor,
            int buoySubIndex)
        {
            if (harbor.rotation < 0 || harbor.rotation > 7)
                throw new ArgumentOutOfRangeException(nameof(harbor), "Rotation index must be between 0 and 7.");

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
            var (source_x, source_y) = GetBuoyWorldCoordinates(sourceHarbor, sourceBuoySubIdx);
            var (target_x, target_y) = GetBuoyWorldCoordinates(targetHarbor, targetBuoySubIdx);

            // Generate a random unique ID for the connection
            int connectionId = GenerateUniqueID();

            // Add connection record using generated harbour IDs instead of array indices
            Buoy_connections.Add((
                connectionId,
                harbourIds[sourceHarborIdx],
                harbourIds[targetHarborIdx],
                source_x,
                source_y,
                target_x,
                target_y
            ));

            // Map connection ID to both source and target buoy slots
            harbourBuoyConnectionIds[sourceHarborIdx, sourceBuoySubIdx] = connectionId;
            harbourBuoyConnectionIds[targetHarborIdx, targetBuoySubIdx] = connectionId;
        }

        readonly List<int[]> established_connections = [];

        public readonly struct State(int row, int col, int direction) : IEquatable<State>
        {
            public int Row { get; } = row;
            public int Col { get; } = col;
            public int Direction { get; } = direction;

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
                return HashCode.Combine(Row, Col, Direction);
            }

            public static bool operator ==(State left, State right)
            {
                return left.Equals(right);
            }

            public static bool operator !=(State left, State right)
            {
                return !(left == right);
            }
        }

        private class Node(DnG_AdK_Mapedit.State state, int gCost, int hCost, DnG_AdK_Mapedit.Node parent = null)
        {
            public State State { get; } = state;
            public int GCost { get; } = gCost;
            public int HCost { get; } = hCost;
            public int FCost => GCost + HCost;
            public Node Parent { get; } = parent;
        }

        // Direction offsets for Odd-R grid (0: E, 1: SE, 2: SW, 3: W, 4: NW, 5: NE)
        private static readonly int[][][] Offsets =
        [
        // Even Rows (source_y % 2 == 0)
        [
            [0, 1],  // 0: East
            [1, 0],  // 1: SE
            [1, -1], // 2: SW
            [0, -1], // 3: West
            [-1, -1],// 4: NW
            [-1, 0]  // 5: NE
        ],
        // Odd Rows (source_y % 2 != 0) - Shifted Right
        [
            [0, 1],  // 0: East
            [1, 1],  // 1: SE
            [1, 0],  // 2: SW
            [0, -1], // 3: West
            [-1, 0], // 4: NW
            [-1, 1]  // 5: NE
        ]
        ];

        /// <summary>
        /// Finds the optimal path from start to goal as an array of [source_x, source_y] coordinates.
        /// </summary>
        /// <param name="heightMap">2D array [row, col] of heights as signed integers.</param>
        /// <param name="start">Start coordinate array [source_x, source_y].</param>
        /// <param name="goal">Goal coordinate array [source_x, source_y].</param>
        /// <param name="establishedPaths">List/collection of previously computed paths to treat as impassable.</param>
        /// <returns>Array of [source_x, source_y] coordinates from start to goal, or null if no valid path exists.</returns>
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
            HashSet<Tuple<int, int>> blockedCoordinates = [];
            if (establishedPaths != null)
            {
                foreach (var coord in establishedPaths)
                {
                    if (coord != null && coord.Length >= 2)
                    {
                        blockedCoordinates.Add(Tuple.Create(coord[1], coord[0])); // source_y = Row, source_x = Col
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

            MinHeapPriorityQueue<Node> openSet = new();
            Dictionary<State, int> gCosts = [];

            State startState = new(startRow, startCol, -1);
            Node startNode = new(startState, 0, GetHeuristic(startRow, startCol, goalRow, goalCol));

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
                    State nextState = new(nextRow, nextCol, dir);

                    if (!gCosts.TryGetValue(nextState, out int existingG) || newGCost < existingG)
                    {
                        gCosts[nextState] = newGCost;
                        int hCost = GetHeuristic(nextRow, nextCol, goalRow, goalCol);
                        Node neighborNode = new(nextState, newGCost, hCost, current);
                        openSet.Enqueue(neighborNode, neighborNode.FCost);
                    }
                }
            }

            if (bestGoalNode == null) return null;

            // Reconstruct path to int[][] array of [source_x, source_y] coordinates
            List<int[]> pathList = [];
            Node curr = bestGoalNode;
            while (curr != null)
            {
                pathList.Add([curr.State.Col, curr.State.Row]); // [source_x, source_y]
                curr = curr.Parent;
            }

            pathList.Reverse();
            return [.. pathList];
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
            private readonly List<Tuple<T, int>> elements = [];

            public int Count => elements.Count;

            public void Enqueue(T item, int priority)
            {
                elements.Add(Tuple.Create(item, priority));
                int ci = elements.Count - 1;
                while (ci > 0)
                {
                    int pi = (ci - 1) / 2;
                    if (elements[ci].Item2 >= elements[pi].Item2) break;
                    (elements[pi], elements[ci]) = (elements[ci], elements[pi]);
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
                    (elements[ci], elements[pi]) = (elements[pi], elements[ci]);
                    pi = ci;
                }
                return frontItem;
            }
        }

        private static readonly byte[][] HarbourRotations =
[
    [0x85, 0x19, 0xAA, 0xAA], // 0: South-west
    [0x03, 0x69, 0xB8, 0xF7], // 1: North-west
    [0xB3, 0xFC, 0x08, 0xCC], // 2: South-east
    [0x53, 0x89, 0xB0, 0xD2], // 3: North-east
    [0xD3, 0x81, 0x9C, 0x6C], // 4: North
    [0xD3, 0xE1, 0x52, 0xF2], // 5: South
    [0xC3, 0x04, 0xC0, 0x8B], // 6: East
    [0x43, 0xB7, 0x84, 0x8F]  // 7: West
];

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

        private static readonly byte[][] CaveTypes =
    [
        [0xD8, 0x70, 0xB3, 0xA3], // 0: AnimalSpawn (Deer, Elk, Rabbit)
        [0x23, 0x89, 0xA5, 0x07], // 1: SheepSpawn
        [0x33, 0x10, 0x75, 0xBE], // 2: DeerSpawn
        [0x53, 0xB9, 0x3D, 0x52], // 3: RabbitSpawn
        [0x72, 0xC8, 0xA5, 0xFC], // 4: __Highland Bear Spawn
        [0x73, 0xC8, 0xA5, 0xFC], // 5: !!!MED Bear Spawn
        [0x74, 0xC8, 0xA5, 0xFC], // 6: Bear Spawn
        [0x75, 0xC8, 0xA5, 0xFC], // 7: --Snow Polar Bear Spawn (+ Mountain Hare)
        [0x76, 0xC8, 0xA5, 0xFC], // 8: __Highland Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Highland Cattle)
        [0x77, 0xC8, 0xA5, 0xFC], // 9: Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Ox)
        [0x78, 0xC8, 0xA5, 0xFC], // 10: !!!MED Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Ox)
        [0x79, 0xC8, 0xA5, 0xFC]  // 11: !!!MED Camel Spawn
    ];

        private static readonly byte[][] DnG_textures =
[
        [0x89, 0xA5, 0x1C, 0xFA], // [0]  !!!MED (RES) rocky earth
        [0x86, 0xA5, 0x1C, 0xFA], // [1]  !!!MED (RES) rocky earth big
        [0x88, 0xA5, 0x1C, 0xFA], // [2]  !!!MED (RES) rocky earth dark
        [0x87, 0xA5, 0x1C, 0xFA], // [3]  !!!MED (RES) rocky plants
        [0x70, 0xA5, 0x1C, 0xFA], // [4]  !!!MED ground 00
        [0x71, 0xA5, 0x1C, 0xFA], // [5]  !!!MED ground 01
        [0x60, 0xA5, 0x1C, 0xFA], // [6]  !!!MED meadow 00
        [0x61, 0xA5, 0x1C, 0xFA], // [7]  !!!MED meadow 01
        [0x62, 0xA5, 0x1C, 0xFA], // [8]  !!!MED meadow 02
        [0x63, 0xA5, 0x1C, 0xFA], // [9]  !!!MED meadow 03
        [0x80, 0xA5, 0x1C, 0xFA], // [10] !!!MED rock
        [0x81, 0xA5, 0x1C, 0xFA], // [11] !!!MED rock big
        [0x83, 0xA5, 0x1C, 0xFA], // [12] !!!MED rock red
        [0x85, 0xA5, 0x1C, 0xFA], // [13] !!!MED rock red big
        [0x84, 0xA5, 0x1C, 0xFA], // [14] !!!MED rock red small
        [0x82, 0xA5, 0x1C, 0xFA], // [15] !!!MED rock small
        [0x90, 0xA5, 0x1C, 0xFA], // [16] !!!MED seaground rock
        [0x91, 0xA5, 0x1C, 0xFA], // [17] !!!MED seaground rock red
        [0x8A, 0xA5, 0x1C, 0xFA], // [18] !!!MED stone ground
        [0x03, 0xDE, 0xCA, 0xDE], // [19] ((00 LAVA 01
        [0x0A, 0xDE, 0xCA, 0xDE], // [20] ((00 LAVA 01 soft
        [0x08, 0xDE, 0xCA, 0xDE], // [21] ((00 LAVA 02
        [0x70, 0xDB, 0x7A, 0xF6], // [22] ((00 LAVA Meadow 00
        [0x70, 0xBB, 0xCA, 0xF1], // [23] ((00 LAVA Sand 
        [0x04, 0xDE, 0xCA, 0xDE], // [27] ((00 LAVA rock
        [0x05, 0xDE, 0xCA, 0xDE], // [28] ((00 LAVA rock big
        [0xB0, 0xFA, 0x87, 0xCA], // [29] ((00 LAVA rock floating lava
        [0x06, 0xDE, 0xCA, 0xDE], // [30] ((00 LAVA rock small
        [0xFF, 0xCA, 0xFE, 0xCA], // [31] (RES) rocky earth
        [0x02, 0xCB, 0xFE, 0xCA], // [32] (RES) rocky earth big
        [0x04, 0xCB, 0xFE, 0xCA], // [33] (RES) rocky earth dark
        [0x03, 0xCB, 0xFE, 0xCA], // [34] (RES) rocky plants
        [0x1A, 0x70, 0x56, 0xCA], // [35] DO NOT USE
        [0x01, 0xDE, 0xCA, 0xDE], // [36] HARBOR
        [0x73, 0x18, 0xD3, 0x76], // [37] border
        [0xC2, 0xFA, 0x45, 0x45], // [38] earth
        [0xC4, 0xFA, 0x45, 0x45], // [39] leaf
        [0xE3, 0xE8, 0xE4, 0xBF], // [40] meadow
        [0xC3, 0xFA, 0x45, 0x45], // [41] meadow bright
        [0xC6, 0xFA, 0x45, 0x45], // [42] meadow dark small
        [0x10, 0x11, 0x5E, 0xDE], // [43] meadow ground
        [0xC5, 0xFA, 0x45, 0x45], // [44] meadow leaf
        [0xC7, 0xFA, 0x45, 0x45], // [45] meadow red flowers
        [0xC1, 0xFA, 0x45, 0x45], // [46] meadow yellow flowers
        [0xFE, 0xAF, 0x0F, 0xD0], // [47] rock
        [0xEF, 0xBE, 0xAD, 0xDE], // [48] rock big
        [0xFE, 0xCA, 0xFE, 0xCA], // [49] rock small
        [0x00, 0xCB, 0xFE, 0xCA], // [50] rock stretched source_x
        [0x01, 0xCB, 0xFE, 0xCA], // [51] rock stretched source_y
        [0x0D, 0xB0, 0xDE, 0xBA], // [52] sand
        [0x0E, 0xB0, 0xDE, 0xBA], // [53] sand stones
        [0x0B, 0xB0, 0xBE, 0xBA], // [54] seaground
        [0xE4, 0x74, 0x33, 0x01], // [55] seaground plants
        [0xE6, 0x74, 0x33, 0x01], // [56] seaground plants rock
        [0xE7, 0x74, 0x33, 0x01], // [57] seaground rock
        [0xE8, 0x74, 0x33, 0x01], // [58] seaground rocky
        [0xE5, 0x74, 0x33, 0x01], // [59] seaground sand
        [0xFF, 0xE0, 0xAD, 0x0F], // [60] snow
        [0x05, 0xCB, 0xFE, 0xCA], // [61] stone ground
        [0xE4, 0x04, 0x00, 0x68], // [62] swamp land
        [0xE6, 0x04, 0x00, 0x68], // [63] swamp meadow (unblocked)
        [0xE5, 0x04, 0x00, 0x68], // [64] swamp water
        [0xB3, 0xD1, 0x6B, 0xFE], // [65] water
        [0xC0, 0xA8, 0x7F, 0x77], // [66] §§Desert earth
        [0xC9, 0xFA, 0x45, 0x45], // [67] §§Desert meadow
        [0x0F, 0xB0, 0xDE, 0xBA], // [68] §§Desert sand dune
        [0x12, 0xB0, 0xDE, 0xBA], // [69] §§Desert sand ripple
        [0x11, 0xB0, 0xDE, 0xBA], // [70] §§Desert sand small dune
        [0x13, 0xB0, 0xDE, 0xBA], // [71] §§Desert sand small ripple
        [0x10, 0xB0, 0xDE, 0xBA]  // [72] §§Desert sand yellow
];

        // Array storing the 4-byte sequences for each terrain entry (index 0 to 40)
        private static readonly byte[][] AdK_textures =
        [
        [0x02, 0x4A, 0xC4, 0x7A], // [0]  __Highland meadow bright
        [0x03, 0x4A, 0xC4, 0x7A], // [1]  __Highland meadow bright rocks
        [0x04, 0x4A, 0xC4, 0x7A], // [2]  __Highland meadow medium
        [0x05, 0x4A, 0xC4, 0x7A], // [3]  __Highland meadow medium rocks
        [0x06, 0x4A, 0xC4, 0x7A], // [4]  __Highland meadow dark
        [0x07, 0x4A, 0xC4, 0x7A], // [5]  __Highland meadow dark rocks
        [0x00, 0x4D, 0xC4, 0x7A], // [6]  __Highland earth fir moss
        [0x01, 0x4D, 0xC4, 0x7A], // [7]  __Highland earth fir
        [0x02, 0x4D, 0xC4, 0x7A], // [8]  __Highland earth
        [0x02, 0x4B, 0xC4, 0x7A], // [9]  __Highland rock
        [0x03, 0x4B, 0xC4, 0x7A], // [10] __Highland rock big
        [0x04, 0x4B, 0xC4, 0x7A], // [11] __Highland (RES) rocky earth
        [0x05, 0x4B, 0xC4, 0x7A], // [12] __Highland rock flat
        [0x06, 0x4B, 0xC4, 0x7A], // [13] __Highland rock dark big
        [0x07, 0x4B, 0xC4, 0x7A], // [14] __Highland rock dark flat
        [0x08, 0x4B, 0xC4, 0x7A], // [15] __Highland rock braid flat
        [0x0D, 0x4B, 0xC4, 0x7A], // [16] __Highland stone ground
        [0x09, 0x4B, 0xC4, 0x7A], // [17] --Snow highland rock much
        [0x0A, 0x4B, 0xC4, 0x7A], // [18] --Snow highland rock
        [0x0B, 0x4B, 0xC4, 0x7A], // [19] --Snow highland rock part
        [0x0C, 0x4B, 0xC4, 0x7A], // [20] --Snow (RES) rocky earth
        [0x0B, 0x4E, 0xC4, 0x7A], // [21] --Snow meadow
        [0x0C, 0x4E, 0xC4, 0x7A], // [22] --Snow meadow snow
        [0x0D, 0x4E, 0xC4, 0x7A], // [23] --Snow meadow snow 2
        [0x0E, 0x4E, 0xC4, 0x7A], // [24] --Snow meadow snow 3
        [0x0F, 0x4E, 0xC4, 0x7A], // [25] --Snow meadow Treeground 80x80,200x200
        [0x10, 0x4E, 0xC4, 0x7A], // [26] --Snow meadow Treeground 125x125
        [0x11, 0x4E, 0xC4, 0x7A], // [27] --Snow meadow Treeground 170x170
        [0x12, 0x4E, 0xC4, 0x7A], // [28] --Snow meadow Treeground 255x255
        [0x10, 0x4C, 0xC4, 0x7A], // [29] __Highland swamp land
        [0x11, 0x4C, 0xC4, 0x7A], // [30] __Highland swamp water
        [0x12, 0x4C, 0xC4, 0x7A], // [31] __Highland swamp meadow (unblocked)
        [0x02, 0x4C, 0xC4, 0x7A], // [32] __Highland seaground rocks
        [0x03, 0x4C, 0xC4, 0x7A], // [33] __Highland seaground rocks dark flat
        [0x04, 0x4C, 0xC4, 0x7A], // [34] __Highland seaground pebbles
        [0x0E, 0x5E, 0xC4, 0x7A], // [35] --Snow Ice Crackles
        [0x0F, 0x5E, 0xC4, 0x7A], // [36] --Snow Ice Crackles Dark
        [0x10, 0x5E, 0xC4, 0x7A], // [37] --Snow Ice Clean
        [0x13, 0x5E, 0xC4, 0x7A], // [38] --Snow Ice Clean Dark
        [0x11, 0x5E, 0xC4, 0x7A], // [39] --Snow medium border
        [0x12, 0x5E, 0xC4, 0x7A]  // [40] --Snow soft border
        ];

        private static readonly int[] DnG_logical_grid_types =
[
    1, //!!!MED StoneResourceA01
    1, //!!!MED StoneResourceA02
    1, //!!!MED StoneResourceA03
    1, //!!!MED StoneResourceA04
    1, //!!!MED StoneResourceA05
    1, //!!!MED StoneResourceA06
    0, //AfricanA
    0, //AsianA
    0, //BirchA
    0, //BirchB
    0, //BirchC
    0, //BroadLeafA
    0, //BroadLeafB
    0, //BroadLeafC
    0, //CypressA
    1, //Field01
    0, //FirA
    0, //FirB
    0, //LavaTreeA
    0, //LavaTreeB
    0, //LavaTreeC
    0, //OliveA
    0, //PalmA
    0, //PalmB
    1, //StoneResourceA01
    1, //StoneResourceA02
    1, //StoneResourceA03
    1, //StoneResourceA04
    1, //StoneResourceA05
    1, //StoneResourceA06
    3, //!!MED rock 1
    3, //!!MED rock 2
    3, //!!MED rock 3
    3, //!!MED rock 4
    3, //((LAVA rock 0
    3, //((LAVA rock 1
    3, //((LAVA rock 2
    3, //Gate01
    3, //rock 1
    3, //rock 2
    3, //rock 3
    3, //rock 4
    2, //Deer
    2, //Elk
    2, //Rabbit
    4, //Beach
    4, //Low Desert Wind
    4, //Middle Desert Wind
    4, //Strong Desert Wind
    4, //bright Forest with birds
    4, //dark Forest with owl
    4, //lava
    4, //meadow with much crickets
    4, //meadow with some crickets and birds
    4, //river
    4, //small water stream
    4, //swamp
    4, //water waves
];

        private static readonly int[] AdK_logical_grid_types =
[
    1, //field_egypt
    0, //__HighlandFirA
    0, //__HighlandFirB
    0, //__HighlandFirC
    0, //--SnowFirA straight pos
    0, //--SnowFirB straight pos
    0, //--SnowFirC straight pos
    0, //--SnowFirA random pos
    0, //--SnowFirB random pos
    0, //--SnowFirC random pos
    0, //--SnowFirD random pos
    0, //--SnowFirE random pos
    0, //--SnowFirF random pos
    0, //Weeping Willow
    0, //Birch New 1
    0, //Birch New 2
    0, //Birch New 3
    0, //Chestnut 1
    0, //Chestnut 2
    0, //Chestnut 3
    0, //Apple Tree 1
    0, //Apple Tree 2
    3, //__Highland rock 1
    3, //__Highland rock 2
    3, //__Highland rock 3
    3, //__Highland rock 4
    3, //--Snow Iceberg 1
    3, //--Snow Iceberg 2
    3, //Tent
    2, //Sheep
    2, //Bear
    2, //Ox
    2, //Highland Cattle
    2, //Goat
    2, //Polarbear
    2, //Mountain Hare
    2, //Boar
    2, //Camel
    4, //hightlands less birds
    4, //hightlands normal birds
    4, //hightlands much birds
    4, //ice
    4, //mountains
    5, //AnimalSpawn (Deer, Elk, Rabbit)
    5, //SheepSpawn
    5, //DeerSpawn
    5, //RabbitSpawn
    5, //__Highland Bear Spawn
    5, //!!!MED Bear Spawn
    5, //Bear Spawn
    5, //--Snow Polar Bear Spawn (+ Mountain Hare)
    5, //__Highland Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Highland Cattle)
    5, //Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Ox)
    5, //!!!MED Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Ox)
    5, //!!!MED Camel Spawn
];

        private static readonly byte[][] DnG_logical_grid =
[
    [0xD0, 0x7F, 0xAB, 0x1D], // 0: !!!MED StoneResourceA01
    [0xD1, 0x7F, 0xAB, 0x1D], // 1: !!!MED StoneResourceA02
    [0xD2, 0x7F, 0xAB, 0x1D], // 2: !!!MED StoneResourceA03
    [0xD3, 0x7F, 0xAB, 0x1D], // 3: !!!MED StoneResourceA04
    [0xD4, 0x7F, 0xAB, 0x1D], // 4: !!!MED StoneResourceA05
    [0xD5, 0x7F, 0xAB, 0x1D], // 5: !!!MED StoneResourceA06
    [0x78, 0x2E, 0xCF, 0xE8 ], // 6: AfricanA
    [0x7C, 0x2E, 0xCF, 0xE8 ], // 7: AsianA
    [0x73, 0xCE, 0x99, 0x7E ], // 8: BirchA
    [0xB3, 0x87, 0x32, 0x06 ], // 9: BirchB
    [0x83, 0xCB, 0x9C, 0x48 ], // 10: BirchC
    [0xB3, 0x47, 0x9F, 0x11 ], // 11: BroadLeafA
    [0xD3, 0x21, 0xCF, 0xE6 ], // 12: BroadLeafB
    [0xC3, 0x44, 0xEF, 0xAD ], // 13: BroadLeafC
    [0x76, 0x2E, 0xCF, 0xE8 ], // 14: CypressA
    [0x9E, 0x4C, 0xED, 0xDF ], // 15: Field01
    [0x73, 0x0E, 0x2D, 0x73], // 16: FirA
    [0x73, 0x0E, 0xCF, 0xE6 ], // 17: FirB
    [0x79, 0x2E, 0xCF, 0xE8 ], // 18: LavaTreeA
    [0x7A, 0x2E, 0xCF, 0xE8 ], // 19: LavaTreeB
    [0x7B, 0x2E, 0xCF, 0xE8 ], // 20: LavaTreeC
    [0x77, 0x2E, 0xCF, 0xE8 ], // 21: OliveA
    [0x74, 0x1E, 0xCF, 0xE7 ], // 22: PalmA
    [0x75, 0x2E, 0xCF, 0xE8 ], // 23: PalmB
    [0x0E, 0xD6, 0x1B, 0x9F ], // 24: StoneResourceA01
    [0x5E, 0x11, 0xB1, 0x5B ], // 25: StoneResourceA02
    [0xEE, 0x5B, 0xEF, 0x21 ], // 26: StoneResourceA03
    [0x8E, 0xCD, 0x46, 0x19 ], // 27: StoneResourceA04
    [0x9E, 0x6A, 0x93, 0x5D ], // 28: StoneResourceA05
    [0xFE, 0xA2, 0x2B, 0xE4], // 29: StoneResourceA06
    [0xA0, 0xC0, 0x91, 0xFA ], // 30: !!MED rock 1
    [0xA1, 0xC0, 0x91, 0xFA ], // 31: !!MED rock 2
    [0xA2, 0xC0, 0x91, 0xFA ], // 32: !!MED rock 3
    [0xA3, 0xC0, 0x91, 0xFA], // 33: !!MED rock 4
    [0xA0, 0xEE, 0xFF, 0xCA ], // 34: ((LAVA rock 0
    [0xA1, 0xEE, 0xFF, 0xCA ], // 35: ((LAVA rock 1
    [0xA2, 0xEE, 0xFF, 0xCA ], // 36: ((LAVA rock 2
    [0xE6, 0xBE, 0xDE, 0xFA ], // 37: Gate01
    [0xA0, 0xE0, 0xAF, 0x6F ], // 38: rock 1
    [0xA1, 0xE0, 0xAF, 0x6F ], // 39: rock 2
    [0xA2, 0xE0, 0xAF, 0x6F ], // 40: rock 3
    [0xA3, 0xE0, 0xAF, 0x6F ], // 41: rock 4
    [0x83, 0xEF, 0x9B, 0x4A ], // 42: Deer
    [0x94, 0x7C, 0x6E, 0x70 ], // 43: Elk
    [0x76, 0x7B, 0x79, 0x41 ], // 44: Rabbit
    [0x73, 0x48, 0xDC, 0x5B ], // 45: Beach
    [0x23, 0x3A, 0xF2, 0x31 ], // 46: Low Desert Wind
    [0x13, 0x3D, 0xEF, 0x67 ], // 47: Middle Desert Wind
    [0xF3, 0x02, 0x56, 0xDF ], // 48: Strong Desert Wind
    [0x23, 0x9A, 0xF5, 0x89 ], // 49: bright Forest with birds
    [0x63, 0x53, 0x8E, 0x11 ], // 50: dark Forest with owl
    [0xA3, 0xBB, 0x52, 0xA9 ], // 51: lava
    [0xD3, 0xD2, 0xAA, 0x5A ], // 52: meadow with much crickets
    [0xD3, 0x37, 0x34, 0x62 ], // 53: meadow with some crickets and birds
    [0xF3, 0x51, 0x5D, 0x87 ], // 54: river
    [0xD3, 0x57, 0x57, 0xF3 ], // 55: small water stream
    [0x63, 0xA7, 0x68, 0x3B ], // 56: swamp
    [0x13, 0x71, 0xA6, 0x00 ]  // 57: water waves
];

        private static readonly byte[][] AdK_logical_grid =
        [
    [0x1A, 0x2E, 0x6B, 0xA2], // 0: field_egypt
    [0x7D, 0x2E, 0xCF, 0xE8 ], // 1: __HighlandFirA
    [0x7E, 0x2E, 0xCF, 0xE8 ], // 2: __HighlandFirB
    [0x7F, 0x2E, 0xCF, 0xE8 ], // 3: __HighlandFirC
    [0x80, 0x2E, 0xCF, 0xE8 ], // 4: --SnowFirA straight pos
    [0x81, 0x2E, 0xCF, 0xE8 ], // 5: --SnowFirB straight pos
    [0x82, 0x2E, 0xCF, 0xE8 ], // 6: --SnowFirC straight pos
    [0x83, 0x2E, 0xCF, 0xE8 ], // 7: --SnowFirA random pos
    [0x84, 0x2E, 0xCF, 0xE8 ], // 8: --SnowFirB random pos
    [0x85, 0x2E, 0xCF, 0xE8 ], // 9: --SnowFirC random pos
    [0x86, 0x2E, 0xCF, 0xE8 ], // 10: --SnowFirD random pos
    [0x87, 0x2E, 0xCF, 0xE8 ], // 11: --SnowFirE random pos
    [0x88, 0x2E, 0xCF, 0xE8 ], // 12: --SnowFirF random pos
    [0x89, 0x2E, 0xCF, 0xE8 ], // 13: Weeping Willow
    [0x8A, 0x2E, 0xCF, 0xE8 ], // 14: Birch New 1
    [0x8B, 0x2E, 0xCF, 0xE8], // 15: Birch New 2
    [0x8C, 0x2E, 0xCF, 0xE8], // 16: Birch New 3
    [0x8D, 0x2E, 0xCF, 0xE8 ], // 17: Chestnut 1
    [0x8E, 0x2E, 0xCF, 0xE8 ], // 18: Chestnut 2
    [0x8F, 0x2E, 0xCF, 0xE8 ], // 19: Chestnut 3
    [0x90, 0x2E, 0xCF, 0xE8 ], // 20: Apple Tree 1
    [0x91, 0x2E, 0xCF, 0xE8 ], // 21: Apple Tree 2
    [0x10, 0xBB, 0x81, 0xA1], // 22: __Highland rock 1
    [0x11, 0xBB, 0x81, 0xA1], // 23: __Highland rock 2
    [0x12, 0xBB, 0x81, 0xA1 ], // 24: __Highland rock 3
    [0x13, 0xBB, 0x81, 0xA1 ], // 25: __Highland rock 4
    [0x20, 0x10, 0x2F, 0xF2 ], // 26: --Snow Iceberg 1
    [0x21, 0x10, 0x2F, 0xF2 ], // 27: --Snow Iceberg 2
    [0x03, 0x2D, 0x66, 0x3D ], // 28: Tent
    [0x33, 0xCA, 0xAE, 0x62 ], // 29: Sheep
    [0x72, 0xA5, 0x1F, 0x10], // 30: Bear
    [0x73, 0xA5, 0x1F, 0x10 ], // 31: Ox
    [0x79, 0xA5, 0x1F, 0x10 ], // 32: Highland Cattle
    [0x74, 0xA5, 0x1F, 0x10 ], // 33: Goat
    [0x75, 0xA5, 0x1F, 0x10 ], // 34: Polarbear
    [0x76, 0xA5, 0x1F, 0x10 ], // 35: Mountain Hare
    [0x77, 0xA5, 0x1F, 0x10 ], // 36: Boar
    [0x78, 0xA5, 0x1F, 0x10 ], // 37: Camel
    [0x53, 0x0D, 0x69, 0xA4 ], // 38: hightlands less birds
    [0x23, 0xB1, 0x89, 0x6C ], // 39: hightlands normal birds
    [0x83, 0xE4, 0x4E, 0x71 ], // 40: hightlands much birds
    [0x63, 0x0A, 0x6C, 0x6E ], // 41: ice
    [0x43, 0xEB, 0x22, 0xF5 ], // 42: mountains
    [0xD8, 0x70, 0xB3, 0xA3 ], // 43: AnimalSpawn (Deer, Elk, Rabbit)
    [0x23, 0x89, 0xA5, 0x07 ], // 44: SheepSpawn
    [0x33, 0x10, 0x75, 0xBE ], // 45: DeerSpawn
    [0x53, 0xB9, 0x3D, 0x52 ], // 46: RabbitSpawn
    [0x72, 0xC8, 0xA5, 0xFC ], // 47: __Highland Bear Spawn
    [0x73, 0xC8, 0xA5, 0xFC ], // 48: !!!MED Bear Spawn
    [0x74, 0xC8, 0xA5, 0xFC ], // 49: Bear Spawn
    [0x75, 0xC8, 0xA5, 0xFC ], // 50: --Snow Polar Bear Spawn (+ Mountain Hare)
    [0x76, 0xC8, 0xA5, 0xFC ], // 51: __Highland Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Highland Cattle)
    [0x77, 0xC8, 0xA5, 0xFC ], // 52: Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Ox)
    [0x78, 0xC8, 0xA5, 0xFC ], // 53: !!!MED Misc Spawn (Deer, Boar, Elk, Rabbit, Goat, Ox)
    [0x79, 0xC8, 0xA5, 0xFC ]  // 54: !!!MED Camel Spawn
        ];

        private static readonly int[] is_lifetime_dng =
[
    0, //!!MED nettle
    0, //!!MED nettle big
    0, //!!MED nettle high
    0, //((LAVA fog
    0, //((LAVA fog high
    0, //((LAVA fog highest
    0, //((LAVA fog vertical
    1, //Coal (few)
    1, //Coal (medium)
    1, //Coal (much)
    0, //DoNotUse-Skull01
    1, //Empty
    1, //Gold (few)
    1, //Gold (medium)
    1, //Gold (much)
    1, //Granit (few)
    1, //Granit (medium)
    1, //Granit (much)
    1, //Iron (few)
    1, //Iron (medium)
    1, //Iron (much)
    1, //Water
    0, //bones0
    0, //bones1
    0, //bones2
    0, //bones3
    0, //bush01
    0, //cactus01
    0, //cactus02
    0, //cactus03
    0, //cactus04
    0, //dead Tree 1
    0, //dead Tree 2
    0, //fern big
    0, //fern medium
    0, //fern small
    0, //fingerpost E
    0, //fingerpost N
    0, //fingerpost NE
    0, //fingerpost NW
    0, //fingerpost S
    0, //fingerpost SE
    0, //fingerpost SW
    0, //fingerpost W
    0, //flower red
    0, //flower red big
    0, //flower red high
    0, //flower violet
    0, //flower violet big
    0, //flower violet high
    0, //flower white
    0, //flower white big
    0, //flower white high
    0, //flower yellow
    0, //flower yellow big
    0, //flower yellow high
    0, //grass translucent
    0, //grass translucent big dark
    0, //grass01
    0, //grass02
    0, //grass03
    0, //grass04
    0, //high flower red
    0, //high flower red big
    0, //high flower white
    0, //high flower white big
    0, //high flower yellow
    0, //high flower yellow big
    0, //mushroom brown
    0, //mushroom brown big
    0, //mushroom red
    0, //mushroom red big
    0, //nettle
    0, //nettle big
    0, //nettle high
    0, //shell
    0, //shell small
    0, //stone01
    0, //stone01 grey
    0, //stone02
    0, //stone02 grey
    0, //stone03
    0, //stone03 grey
    0, //stone04
    0, //stone04 grey
    0, //swamp calmus 01
    0, //swamp calmus 02
    0, //swamp calmus 03
    0, //swampthing01
    0, //swampthing02
    0, //waterlily 1
    0, //waterlily 2
    0, //waterplant 1
    0, //waterplant 2
    0, //waterplant 3
    0, //wreck
    0  //wreck big
];

        private static readonly int[] is_lifetime_adk =
[
    0, //Chest
    0, //OpenChest
    1, //Coal (endless)
    1, //Iron (endless)
    1, //Gold (endless)
    1, //Granite (endless)
    1, //Gemstones (few)
    1, //Gemstones (medium)
    1, //Gemstones (much)
    1, //Gemstones (endless)
    1, //Salt (few)
    1, //Salt (medium)
    1, //Salt (much)
    1, //Salt (endless)
    0, //--Snow Ice Floe 01 moving
    0, //--Snow Ice Floe 01 static
    0, //--Snow Ice Floe 02 static
    0, //--Snow Ice Floe 03 static
    0, //--Snow Ice Floe 04 static
    0, //--Snow Ice Floe 05 static
    0, //--Snow Ice Floe 06 moving
    0, //--Snow Ice Floe 07 moving
    0, //--Snow Ice Floe 08 moving
    0, //--Snow Ice Floe 09 moving
    0, //__Highland fern big
    0, //__Highland fern miedium
    0, //__Highland fern small
    0, //__Highland nettle
    0, //__Highland nettle big
    0, //__Highland nettle high
    0, //__Highland Edelweiss 1
    0, //__Highland Edelweiss 2
    0, //__Highland Edelweiss 3
    0, //__Highland Snowdrop
    0, //__Highland Crocus
    0, //__Highland Foundling 1
    0, //__Highland Foundling 2
    0, //__Highland Foundling 3
    0, //__Highland Underwater Foundling 1
    0, //__Highland Underwater Foundling 2
    0, //__Highland Underwater Foundling 3
    0, //__Highland swamp calmus 01
    0, //__Highland swamp calmus 02
    0, //__Highland swamp calmus 03
    0, //__Highland Fog 01
    0, //__Highland Fog 02
    0, //Male Duck
    0  //Female Duck
];

        private static readonly byte[][] doodads_dng =
[
    [0x30, 0x42, 0xA7, 0xBC], //!!MED nettle
    [0x31, 0x42, 0xA7, 0xBC], //!!MED nettle big
    [0x32, 0x42, 0xA7, 0xBC], //!!MED nettle high
    [0xC0, 0x17, 0xFF, 0xAA], //((LAVA fog
    [0xC1, 0x17, 0xFF, 0xAA], //((LAVA fog high
    [0xC2, 0x17, 0xFF, 0xAA], //((LAVA fog highest
    [0xC3, 0x17, 0xFF, 0xAA], //((LAVA fog vertical
    [0x93, 0xB7, 0xEE, 0x90], //Coal (few)
    [0x43, 0x61, 0x09, 0xC5], //Coal (medium)
    [0xF3, 0x6F, 0xAD, 0x00], //Coal (much)
    [0x13, 0x0A, 0xCB, 0xDA], //DoNotUse-Skull01
    [0x43, 0x23, 0xF4, 0x28], //Empty
    [0xD3, 0x1A, 0x77, 0x96], //Gold (few)
    [0xA3, 0xC3, 0x6A, 0xE0], //Gold (medium)
    [0x23, 0xD1, 0x12, 0xE8], //Gold (much)
    [0x93, 0xA1, 0x24, 0x31], //Granit (few)
    [0x53, 0x0D, 0xCF, 0x8E], //Granit (medium)
    [0x73, 0x47, 0x68, 0x17], //Granit (much)
    [0x63, 0xE5, 0xDB, 0x45], //Iron (few)
    [0xE3, 0x52, 0x3A, 0xD3], //Iron (medium)
    [0x23, 0xF1, 0x82, 0x4B], //Iron (much)
    [0x43, 0xA3, 0x1A, 0x12], //Water
    [0x9D, 0xA7, 0xF5, 0xD5], //bones0
    [0xCD, 0x22, 0x51, 0x17], //bones1
    [0xAD, 0xA4, 0x45, 0x72], //bones2
    [0x5D, 0x6E, 0x83, 0x37], //bones3
    [0xDE, 0x2E, 0x27, 0x6B], //bush01
    [0xEE, 0x50, 0x20, 0x48], //cactus01
    [0x0E, 0x8B, 0x06, 0xA3], //cactus02
    [0x7E, 0x5B, 0x18, 0xEC], //cactus03
    [0x39, 0xAE, 0xF5, 0x89], //cactus04
    [0x1E, 0xE8, 0xBF, 0xF2], //dead Tree 1
    [0x1F, 0xE8, 0xBF, 0xF2], //dead Tree 2
    [0xCE, 0x31, 0x24, 0xA1], //fern big
    [0x33, 0xAE, 0xF5, 0x89], //fern medium
    [0x34, 0xAE, 0xF5, 0x89], //fern small
    [0xE0, 0xF1, 0xA0, 0xAA], //fingerpost E
    [0xE6, 0xF1, 0xA0, 0xAA], //fingerpost N
    [0xE7, 0xF1, 0xA0, 0xAA], //fingerpost NE
    [0xE5, 0xF1, 0xA0, 0xAA], //fingerpost NW
    [0xE2, 0xF1, 0xA0, 0xAA], //fingerpost S
    [0xE1, 0xF1, 0xA0, 0xAA], //fingerpost SE
    [0xE3, 0xF1, 0xA0, 0xAA], //fingerpost SW
    [0xE4, 0xF1, 0xA0, 0xAA], //fingerpost W
    [0xE4, 0xAF, 0xA1, 0x0F], //flower red
    [0xE5, 0xAF, 0xA1, 0x0F], //flower red big
    [0xE6, 0xAF, 0xA1, 0x0F], //flower red high
    [0xED, 0xAF, 0xA1, 0x0F], //flower violet
    [0xEE, 0xAF, 0xA1, 0x0F], //flower violet big
    [0xEF, 0xAF, 0xA1, 0x0F], //flower violet high
    [0xE7, 0xAF, 0xA1, 0x0F], //flower white
    [0xE8, 0xAF, 0xA1, 0x0F], //flower white big
    [0xE9, 0xAF, 0xA1, 0x0F], //flower white high
    [0xEA, 0xAF, 0xA1, 0x0F], //flower yellow
    [0xEB, 0xAF, 0xA1, 0x0F], //flower yellow big
    [0xEC, 0xAF, 0xA1, 0x0F], //flower yellow high
    [0xF0, 0xAF, 0xA1, 0x0F], //grass translucent
    [0xF1, 0xAF, 0xA1, 0x0F], //grass translucent big dark
    [0xAE, 0x37, 0xD1, 0x3A], //grass01
    [0xAF, 0x37, 0xD1, 0x3A], //grass02
    [0xB0, 0x37, 0xD1, 0x3A], //grass03
    [0xB1, 0x37, 0xD1, 0x3A], //grass04
    [0x36, 0xAE, 0xF5, 0x89], //high flower red
    [0x35, 0xAE, 0xF5, 0x89], //high flower red big
    [0xBE, 0x34, 0xD4, 0x04], //high flower white
    [0xFE, 0xCD, 0x49, 0xFB], //high flower white big
    [0x38, 0xAE, 0xF5, 0x89], //high flower yellow
    [0x37, 0xAE, 0xF5, 0x89], //high flower yellow big
    [0xF2, 0xEF, 0xAD, 0xAC], //mushroom brown
    [0xF3, 0xEF, 0xAD, 0xAC], //mushroom brown big
    [0xF0, 0xEF, 0xAD, 0xAC], //mushroom red
    [0xF1, 0xEF, 0xAD, 0xAC], //mushroom red big
    [0xE1, 0xAF, 0xA1, 0x0F], //nettle
    [0xE2, 0xAF, 0xA1, 0x0F], //nettle big
    [0xE3, 0xAF, 0xA1, 0x0F], //nettle high
    [0x10, 0xE3, 0x11, 0xFA], //shell
    [0x11, 0xE3, 0x11, 0xFA], //shell small
    [0x4E, 0x1F, 0x5C, 0x45], //stone01
    [0x4F, 0x1F, 0x5C, 0x45], //stone01 grey
    [0x3E, 0x02, 0x36, 0xEA], //stone02
    [0x3F, 0x02, 0x36, 0xEA], //stone02 grey
    [0x8E, 0xD8, 0x41, 0x9F], //stone03
    [0x8F, 0xD8, 0x41, 0x9F], //stone03 grey
    [0x6E, 0xBE, 0xCB, 0xA7], //stone04
    [0x6F, 0xBE, 0xCB, 0xA7], //stone04 grey
    [0xE3, 0xBE, 0xDE, 0xFA], //swamp calmus 01
    [0xE4, 0xBE, 0xDE, 0xFA], //swamp calmus 02
    [0xE5, 0xBE, 0xDE, 0xFA], //swamp calmus 03
    [0xE1, 0xBE, 0xDE, 0xFA], //swampthing01
    [0xE2, 0xBE, 0xDE, 0xFA], //swampthing02
    [0x30, 0xA2, 0xD6, 0xF1], //waterlily 1
    [0x31, 0xA2, 0xD6, 0xF1], //waterlily 2
    [0x30, 0xA2, 0xC6, 0xF1], //waterplant 1
    [0x31, 0xA2, 0xC6, 0xF1], //waterplant 2
    [0x32, 0xA2, 0xC6, 0xF1], //waterplant 3
    [0x10, 0xE2, 0x11, 0xFA], //wreck
    [0x11, 0xE2, 0x11, 0xFA]  //wreck big
];

        private static readonly byte[][] doodads_adk =
[
    [0x74, 0xBE, 0x45, 0x7A], //Chest
    [0x34, 0xBF, 0xF9, 0x16], //OpenChest
    [0x63, 0xA0, 0x5A, 0xC5], //Coal (endless)
    [0x73, 0x9D, 0x5D, 0x8F], //Iron (endless)
    [0x63, 0xC0, 0xCA, 0x28], //Gold (endless)
    [0xE3, 0xD2, 0x45, 0xB2], //Granite (endless)
    [0x33, 0xD2, 0x28, 0x4E], //Gemstones (few)
    [0x03, 0x76, 0x96, 0xE8], //Gemstones (medium)
    [0x53, 0x6C, 0xC5, 0x2E], //Gemstones (much)
    [0x03, 0x0D, 0xDF, 0x3A], //Gemstones (endless)
    [0xC3, 0xDC, 0x20, 0xF2], //Salt (few)
    [0xC3, 0x3C, 0xD7, 0x77], //Salt (medium)
    [0xF3, 0x58, 0x23, 0xBB], //Salt (much)
    [0xA3, 0x7E, 0x36, 0x32], //Salt (endless)
    [0x01, 0xBB, 0x81, 0xA1], //--Snow Ice Floe 01 moving
    [0x02, 0xBB, 0x81, 0xA1], //--Snow Ice Floe 01 static
    [0x03, 0xBB, 0x81, 0xA1], //--Snow Ice Floe 02 static
    [0x04, 0xBB, 0x81, 0xA1], //--Snow Ice Floe 03 static
    [0x05, 0xBB, 0x81, 0xA1], //--Snow Ice Floe 04 static
    [0x06, 0xBB, 0x81, 0xA1], //--Snow Ice Floe 05 static
    [0x07, 0xBB, 0x81, 0xA1], //--Snow Ice Floe 06 moving
    [0x08, 0xBB, 0x81, 0xA1], //--Snow Ice Floe 07 moving
    [0x09, 0xBB, 0x81, 0xA1], //--Snow Ice Floe 08 moving
    [0x0A, 0xBB, 0x81, 0xA1], //--Snow Ice Floe 09 moving
    [0x20, 0xBB, 0x81, 0xA1], //__Highland fern big
    [0x21, 0xBB, 0x81, 0xA1], //__Highland fern miedium
    [0x22, 0xBB, 0x81, 0xA1], //__Highland fern small
    [0x30, 0xBB, 0x81, 0xA1], //__Highland nettle
    [0x31, 0xBB, 0x81, 0xA1], //__Highland nettle big
    [0x32, 0xBB, 0x81, 0xA1], //__Highland nettle high
    [0x33, 0xBB, 0x81, 0xA1], //__Highland Edelweiss 1
    [0x34, 0xBB, 0x81, 0xA1], //__Highland Edelweiss 2
    [0x35, 0xBB, 0x81, 0xA1], //__Highland Edelweiss 3
    [0x36, 0xBB, 0x81, 0xA1], //__Highland Snowdrop
    [0x37, 0xBB, 0x81, 0xA1], //__Highland Crocus
    [0x40, 0xBB, 0x81, 0xA1], //__Highland Foundling 1
    [0x41, 0xBB, 0x81, 0xA1], //__Highland Foundling 2
    [0x42, 0xBB, 0x81, 0xA1], //__Highland Foundling 3
    [0x43, 0xBB, 0x81, 0xA1], //__Highland Underwater Foundling 1
    [0x44, 0xBB, 0x81, 0xA1], //__Highland Underwater Foundling 2
    [0x45, 0xBB, 0x81, 0xA1], //__Highland Underwater Foundling 3
    [0x40, 0xBC, 0x81, 0xA1], //__Highland swamp calmus 01
    [0x41, 0xBC, 0x81, 0xA1], //__Highland swamp calmus 02
    [0x42, 0xBC, 0x81, 0xA1], //__Highland swamp calmus 03
    [0x00, 0xBD, 0x81, 0xA1], //__Highland Fog 01
    [0x01, 0xBD, 0x81, 0xA1], //__Highland Fog 02
    [0x10, 0xBD, 0x81, 0xA1], //Male Duck
    [0x11, 0xBD, 0x81, 0xA1]  //Female Duck
];
    }
}