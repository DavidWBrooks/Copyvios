using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
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
        double StaticHeight;

        public MainWindow()
        {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            Regex.CacheSize = 20;
            InitializeComponent();
        }

        private void StartComparison(object sender, RoutedEventArgs e)
        {
            if (!BothFieldsPresent()) {
                MessageBox.Show("Provide both fields");
                return;
            }
            DoComparison();
        }

        private void DoComparison()
        {
            DateTime starttime = DateTime.Now;
            string article = articleTitle.Text;
            string url = URL.Text;
            if (!Regex.IsMatch(url, "^https?://.", RegexOptions.IgnoreCase)) {
                MessageBox.Show("URL must start with http[s]://");
                return;
            }

            string wpAction =
            "https://en.wikipedia.org/w/api.php?action=query&format=xml&prop=revisions&rvslots=main&rvprop=content&titles=" +
            article;
            string urlhttp, wphttp;

            // Testing shows one HttpClient can handle two simultaneous requests (documentation isn't clear on that)
            using (HttpClient client = new HttpClient()) {
                using (Task<string> urldownload = client.GetStringAsync(url),
                                    wpdownload = client.GetStringAsync(wpAction)) {

                    // As we will wait for both, the order doesn't matter
                    string what = "Reading the URL: ";
                    try {
                        urlhttp = urldownload.Result;
                        what = "Reading the Wikipedia article: ";
                        wphttp = wpdownload.Result;
                    }
                    catch (AggregateException aex) {
                        MessageBox.Show(what + aex.InnerException.Message);
                        return;
                    }
                    catch (Exception ex) {
                        MessageBox.Show(what + ex.Message);
                        return;
                    }
                }
            }

            string wpcontent, urlcontent;

            try {
                wpcontent = StripWP(wphttp);
                urlcontent = StripUrl(urlhttp);
            }
            catch (Exception ex) {  // surface any fatal error
                MessageBox.Show(ex.Message);
                return;
            }

            WPHeading.Content = article;
            Reload(wpcontent, urlcontent);

            if (wpcontent.Length == 0 || urlcontent.Length == 0) {   // Weird, right?
                Status("Nothing to see here");
                return;
            }

            List<Chunk> wpchunks = Matcher.Reducer(wpcontent);
            List<Chunk> urlchunks = Matcher.Reducer(urlcontent);

            // Mark the chunks that match the opposite number
            Matcher.Marker(wpchunks, urlchunks);

            // Use the matched chunks to map to a bitmap of the original text
            bool[] wpmap = new bool[wpcontent.Length];
            bool[] urlmap = new bool[urlcontent.Length];

            Matcher.Mapper(wpchunks, wpmap);
            Matcher.Mapper(urlchunks, urlmap);

            // Finally generate a sequence of Runs
            IEnumerable<Run> wpruns = Matcher.Markup(wpcontent, wpmap);
            IEnumerable<Run> urlruns = Matcher.Markup(urlcontent, urlmap);

            Reload(wpruns, urlruns);

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

            return FinalTrim(wptext);
        }

        private string StripUrl(string urlhttp)
        {
            string result;

            // Extract body if there is one
            Match m = Regex.Match(urlhttp, "<body[^>]*>(.*)</body>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            result = m.Success ? m.Groups[1].Value : urlhttp;

            // Strip to the first para marker
            int pmarker = result.IndexOf("<p>", StringComparison.CurrentCultureIgnoreCase);
            if (pmarker >= 0) {
                result = result.Substring(pmarker + 3);
            }

            // Strip from the last end-paragraph marker
            pmarker = result.LastIndexOf("</p>", StringComparison.CurrentCultureIgnoreCase);
            if (pmarker >= 0) {
                result = result.Substring(0, pmarker);
            }

            // Replace paragraph markers with newline and remove most remaining HTML markup

            result = result.Replace("<p>", "\n").Replace("<P>", "\n");
            result = Regex.Replace(result, "<[^>]*>", "");

            return FinalTrim(result);
        }

        // This is a little ad-hoc, and deals with some odd circumstances that result from stripping
        private static string FinalTrim(string str)
        {
            string result = WebUtility.HtmlDecode(str);

            // Whitespace cleanup is getting a little out of hand; needs another look
            // Common artefact of template suppression:
            result = Regex.Replace(result, @"^[ \t]*$", String.Empty, RegexOptions.Multiline);

            while (true) {
                int len = result.Length;
                result = result.Replace("\n\n\n", "\n\n");
                if (result.Length == len) break;
            }

            // There could be leading and trailing whitespace, but need a final nl for the
            // scroller to display the last line
            return result.Trim() + '\n';
        }

        private void Status(string message)
        {
            Progress.Content = message;
        }

        private void Reload(IEnumerable<Run> wpruns, IEnumerable<Run> urlruns)
        {
            WPPara.Inlines.Clear();
            WPPara.Inlines.AddRange(wpruns);
            WPViewer.ScrollToHome();
            URLPara.Inlines.Clear();
            URLPara.Inlines.AddRange(urlruns);
            URLViewer.ScrollToHome();
        }

        private void Reload(string wpstring, string ebstring)
        {
            Reload(new[] { new Run(wpstring) }, new[] { new Run(ebstring) });
        }

        private void Resized(object sender, SizeChangedEventArgs e)
        {
            double newHeight = this.ActualHeight - StaticHeight;
            WPViewer.Height = newHeight;
            URLViewer.Height = newHeight;
        }

        private void Rendered(object sender, EventArgs e)
        {
            StaticHeight = this.ActualHeight - WPViewer.ActualHeight;
            SizeChanged += Resized;

            if (BothFieldsPresent()) {
                DoComparison();
            }
        }

        private bool BothFieldsPresent()
        {
            return !(String.IsNullOrWhiteSpace(articleTitle.Text) || String.IsNullOrWhiteSpace(URL.Text));
        }
    }
}
