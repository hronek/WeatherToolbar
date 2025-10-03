
using System;
using System.Windows.Forms;
using System.Threading;

namespace WeatherToolbar
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            bool created;
            using (var mutex = new Mutex(true, "WeatherToolbar.SingleInstance", out created))
            {
                if (!created)
                {
                    // already running
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new TrayAppContext());
            }
        }
    }
}
