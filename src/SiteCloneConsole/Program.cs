using System;
using System.Threading.Tasks;
using Whikiey.SiteClone;

namespace Whikiey.SiteCloneConsole
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var siteRoot = args.Length == 0 ? InputLine("Please input the root URL(http://www.example.com):") : args[0];
            SiteCrawler crawler = new(siteRoot, "output");
            await crawler.DownloadAsync();
        }

        private static string InputLine(string message)
        {
            if (message != null)
                Console.WriteLine(message);
            return Console.ReadLine();
        }
    }
}
