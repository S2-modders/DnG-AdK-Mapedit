using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;

namespace DnG_AdK_Mapedit
{
    internal static class Program
    {
        /// <summary>
        /// Główny punkt wejścia dla aplikacji.
        /// </summary>
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new DnG_AdK_Mapedit());
        }
    }
}
