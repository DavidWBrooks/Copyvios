using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Xml;

namespace Copyvios
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        readonly HttpClient client = new HttpClient();

        double StaticHeight;

        public MainWindow()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Regex.CacheSize = 20;
            InitializeComponent();
        }

        // Tried doing this with async work but got weird cross-thread errors
        private void StartComparison(object sender, RoutedEventArgs e)
        {
            DoComparison();
        }

        private void DoComparison()
        {
            DateTime starttime = DateTime.Now;
            string article = articleTitle.Text;
            string url = URL.Text;
            if (String.IsNullOrWhiteSpace(article) || String.IsNullOrWhiteSpace(url)) {
                MessageBox.Show("Provide both fields");
                return;
            }

            if (!Regex.IsMatch(url, "^https?://", RegexOptions.IgnoreCase)) {
                url = "https://en.wikisource.org/wiki/1911_Encyclopædia_Britannica/" + Uri.EscapeDataString(url.Replace(' ', '_'));
            }

            string wpAction =
                "https://en.wikipedia.org/w/api.php?action=query&format=xml&prop=revisions&rvslots=main&rvprop=content&titles=" + 
                article;
            string ebhttp, wphttp;

            string what = "Reading the URL: ";
            try {
                ebhttp = client.GetStringAsync(url).Result;
                what = "Reading the Wikipedia article: ";
                wphttp = client.GetStringAsync(wpAction).Result;
            }
            catch (AggregateException aex) {
                MessageBox.Show(what + aex.InnerException.Message);
                return;
            }
            catch (Exception ex) {
                MessageBox.Show(what + ex.Message);
                return;
            }

            string wpcontent, ebcontent;

            try {
                wpcontent = StripWP(wphttp);
                ebcontent = StripEB(ebhttp);
            }
            catch (Exception ex) {  // surface any fatal error
                MessageBox.Show(ex.Message);
                return;
            }

            WPHeading.Content = article;
            Reload(wpcontent, ebcontent);

            if (wpcontent.Length == 0 || ebcontent.Length == 0) {   // Weird, right?
                Status("Nothing to see here");
                return;
            }

            List<Chunk> wpchunks = Matcher.Reducer(wpcontent);
            List<Chunk> ebchunks = Matcher.Reducer(ebcontent);

            // Mark the chunks that match the opposite number
            Matcher.Marker(wpchunks, ebchunks);

            // Use the matched chunks to map to a bitmap of the original text
            bool[] wpmap = new bool[wpcontent.Length];
            bool[] ebmap = new bool[ebcontent.Length];

            Matcher.Mapper(wpchunks, wpmap);
            Matcher.Mapper(ebchunks, ebmap);

            // Finally generate a sequence of Runs
            IEnumerable<Run> wpruns = Matcher.Markup(wpcontent, wpmap);
            IEnumerable<Run> ebruns = Matcher.Markup(ebcontent, ebmap);
            Reload(wpruns, ebruns);

            double elapsed = (DateTime.Now - starttime).TotalSeconds;
            Status($"Total time: {elapsed:N1} seconds");
        }

        private void WindowLoaded(object sender, RoutedEventArgs e)
        {
            string[] args = Environment.GetCommandLineArgs();
            if (args.Length > 1) articleTitle.Text = args[1];
            if (args.Length > 2) URL.Text = args[2];
        }

        private string StripWP(string wphttp)
        {
            XmlDocument doc = new XmlDocument();
            doc.LoadXml(wphttp);
            XmlNodeList slotels = doc.GetElementsByTagName("slot");
            if (slotels.Count == 0) {
                throw new ApplicationException("Wikipedia query didn't return an article");
            }
            string wptext = slotels[0].InnerText;

            wptext = Regex.Replace(wptext, "<!--.*?-->", "");
            wptext = Regex.Replace(wptext, "<gallery.*?</gallery>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            wptext = Regex.Replace(wptext, "\\[\\[Category.*?]]\n?", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            wptext = Regex.Replace(wptext, @"\[\[[^]]+\|(.+?)]]", (m) => { return m.Groups[1].Value; }, RegexOptions.Singleline);
            wptext = wptext.Replace("[", "").Replace("]", "");
            wptext = Regex.Replace(wptext, @"<ref[^>]*/>", "", RegexOptions.IgnoreCase);
            wptext = Regex.Replace(wptext, "<ref.+?</ref>", "", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            wptext = Regex.Replace(wptext, "'{2,}", "");

            // Fun: walk a pair of characters at a time, detecting nesting {{}} sets
            int templateDepth = 0;
            StringBuilder wpsb = new StringBuilder(wptext.Length);
            // Guard
            wptext += '\n';

            for (int i = 0; i < wptext.Length - 1; i++) {
                char c1 = wptext[i];
                char c2 = wptext[i + 1];
                if (c1 == '{' && c2 == '{') templateDepth++;
                if (templateDepth == 0) wpsb.Append(c1);
                if (c1 == '}' && c2 == '}' && templateDepth > 0) {
                    if (--templateDepth == 0) {
                        wpsb.Append(" ");   // Treat that template as a word-breaker in case it's embedded (think {{mdash}}, {{snd}})
                    }
                    i++;
                }
            }

            // Because I may just have inserted multiple spaces:
            wptext = Regex.Replace(wpsb.ToString(), " {2,}", " ");

            // Now-empty bulleted lists are fairly common
            wptext = Regex.Replace(wptext.ToString(), @"^\s*\*\s*$", "", RegexOptions.Multiline);
            wptext = WebUtility.HtmlDecode(wptext);

            // Finally trim runs of newline
            while (true) {
                int len = wptext.Length;
                wptext = wptext.Replace("\n\n\n", "\n\n");
                if (wptext.Length == len) break;
            }

            return wptext.TrimStart('\n');
        }

        private string StripEB(string ebhttp)
        {
            string result;

            // Extract body if there is one
            Match m = Regex.Match(ebhttp, "<body[^>]*>(.*)</body>", RegexOptions.Singleline);
            result = m.Success ? m.Groups[1].Value : ebhttp;

            // Strip to the first para marker
            int pmarker = result.IndexOf("<p>");
            if (pmarker >= 0) {
                result = result.Substring(pmarker + 3);
            }

            // Strip from the last end-paragraph marker
            pmarker = result.LastIndexOf("</p>");
            if (pmarker >= 0) {
                result = result.Substring(0, pmarker);
            }

            // Replace paragraph markers with newline and remove most remaining HTML markup
            // Does this do the right thing with italics etc?

            result = result.Replace("<p>", "\n");
            result = Regex.Replace(result, "<[^>]*>", "");
            while (true) {
                int len = result.Length;
                result = result.Replace("\n\n\n", "\n\n");
                if (result.Length == len) break;
            }

            return WebUtility.HtmlDecode(result);
        }

        private void Status(string message)
        {
            Progress.Content = message;
        }

        private void Reload(IEnumerable<Run> wpruns, IEnumerable<Run> ebruns)
        {
            WPPara.Inlines.Clear();
            WPPara.Inlines.AddRange(wpruns);
            EBPara.Inlines.Clear();
            EBPara.Inlines.AddRange(ebruns);
        }

        private void Reload(string wpstring, string ebstring)
        {
            Reload(new[] { new Run(wpstring) }, new[] { new Run(ebstring) });
        }

        private void Resized(object sender, SizeChangedEventArgs e)
        {
            double newHeight = this.ActualHeight - StaticHeight;
            WPViewer.Height = newHeight;
            EBViewer.Height = newHeight;
        }

        private void Rendered(object sender, EventArgs e)
        {
            StaticHeight = this.ActualHeight - WPViewer.ActualHeight;
            SizeChanged += Resized;

            if (articleTitle.Text.Length > 0 && URL.Text.Length > 0) {
                DoComparison();
            }
        }
    }
}
