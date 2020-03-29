using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Documents;
using System.Windows.Media;

namespace Copyvios
{
    // A single word
    public class Word
    {
        private static readonly string[] commonWords = new[] {
            "a", "an", "the", "in", "on",
            "it", "at", "to", "he", "she",
            "as", "is", "was", "are", "of",
            "by", "for", "or", "and", "s" };    // s seems to be a word if preceded by apostrophe
        private static readonly int[] commonHashes = commonWords.Select(w => w.GetHashCode()).ToArray();

        public int StartPos { get; }
        public int Length { get; }
        public int Hash { get; }
        public bool IsCommon { get; }

        public Word(int s, int l, string word)
        {
            StartPos = s;
            Length = l;
            Hash = word.GetHashCode();
            IsCommon = commonHashes.Contains(Hash);
        }
    }

    // A single nGram
    public class Chunk
    {
        public int StartPos { get; }    // Position in the original text
        public int Length { get; }      // Length in the original text
        public UInt64 Hash { get; }
        public bool IsMatch { get; set; } = false;

        public Chunk(int s, int l, UInt64 h)
        {
            StartPos = s;
            Length = l;
            Hash = h;
        }
    }

    public static class Matcher
    {
        const int minGram = 3;
        const int maxGram = 5;

        const Int64 guardhash = 0;
        static readonly Brush highlighter = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0xAA));//
        static readonly Brush mediumlighter = new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0xB0));

        // Produce a list of words as their hashes
        static Word[] WordReduce(string text)
        {
            MatchCollection matches = Regex.Matches(text.ToLower(), @"\w+");
            List<Word> result = new List<Word>(matches.Count);
            foreach (Match m in matches) {
                result.Add(new Word(m.Index, m.Length, m.Value));
            }

            // Some guards that should work with the above logic
            for (int i = minGram; i < maxGram; i++) {
                result.Add(new Word(text.Length, 0, "a"));
            }
            return result.ToArray();
        }

        // Produce a list of nGrams
        public static List<Chunk> Reducer(string text)
        {
            Word[] wordlist = WordReduce(text);
            List<Chunk> result = new List<Chunk>(wordlist.Length + 1);

            // Work through word sequences, ending 5 away from the end of the list
            // Yes, adjacent grams will have overlapping words.
            // When accumulating, if you don't shift-left, the words can be in any order.
            // But forget the shift if you actually want the "unordered" logic (in which case the hash can be 32-bit).
            // If minGram >5, an 8-bit shift could lose data (in which case either shift fewer bits, or rotate).
            for (int i = 0; i <= (wordlist.Length - maxGram); i++) {
                Word thisword = wordlist[i];
                int start = thisword.StartPos;
                UInt64 newgram = 0;

                // After some experimentation with several different algorithms, we seem to get the best results
                // by combining two approaches, which also have few false positives:
                // Combine 4 consecutive words, if at least one is significant
                // Combine 3 significant words, skipping as many common words as necessary
                bool sig = false;
                for (int j = 0; j < 4; j++) {
                    thisword = wordlist[i + j];
                    newgram = (newgram << 8) ^ (UInt32)thisword.Hash;
                    if (!thisword.IsCommon) sig = true;
                }
                if (sig && newgram != 0) {  // Although newgram can theoretically be 0, testing shows it very rare.
                    result.Add(new Chunk(start, thisword.StartPos - start + thisword.Length, newgram));
                }

                newgram = 0;
                int wordsingram = 0;
                for (int j = i; j < wordlist.Length; j++) {
                    thisword = wordlist[j];
                    if (!thisword.IsCommon) {
                        newgram = (newgram << 8) ^ (UInt32)thisword.Hash;
                        if (++wordsingram == minGram)
                            break;
                    }
                }
                if (newgram != 0) {     // Although this can theoretically be 0, testing shows it very rare.
                    result.Add(new Chunk(start, thisword.StartPos - start + thisword.Length, newgram));
                }
            }

            result.Add(new Chunk(text.Length, 0, guardhash));   // Guard a possible edge case
            return result;
        }

        // Match and mark both sides. Using a more efficient lookup with a dictionary of lists could make this
        // O(n) instead of O(n^2), but this simpler search is rarely expensive compared with web access. Also, it
        // seemed that the structures needed to support O(n) were expensive themselves.
        // Using a 2x parallelization helps (on a 2-core box) but only by about 20% even on a large page. You
        // probably have at least two cores, and we trust the thread pool will use them.
        // Testing shows that two explicit tasks is faster than a single side-task or specific thread, go figure.
        // There may be occasionaly contention in setting isMatch, but it can only ever be setting true.
        public static void Marker(IList<Chunk> wpchunks, IList<Chunk> ebchunks)
        {
            int half = wpchunks.Count / 2;

            Task[] subtasks = new Task[2];
            subtasks[0] = Task.Run(() => MarkSubset(wpchunks, ebchunks, 0, half));
            subtasks[1] = Task.Run(() => MarkSubset(wpchunks, ebchunks, half, wpchunks.Count));

            Task.WaitAll(subtasks);
        }

        private static void MarkSubset(IList<Chunk> wpchunks, IList<Chunk> ebchunks, int startind, int end)
        {
            for (int i = startind; i < end; i++) {
                Chunk wpchunk = wpchunks[i];
                foreach (Chunk ebchunk in ebchunks) {
                    if (wpchunk.Hash == ebchunk.Hash) {
                        wpchunk.IsMatch = ebchunk.IsMatch = true;
                    }
                }
            }
        }

        // Consolidate chunks into an array of on/off settings
        public static void Mapper(List<Chunk> chunks, bool[] bitmap)
        {
            foreach (Chunk chunk in chunks.Where(c => c.IsMatch)) {
                int ind = chunk.StartPos;
                int length = chunk.Length;
                while (length-- > 0) {
                    bitmap[ind++] = true;
                }
            }
        }

        // Provide a sequence of Runs
        internal static IEnumerable<Run> Markup(string content, bool[] map)
        {
            // I think we already eliminated this, but to be sure...
            if (content.Length == 0) {
                return new Run[0];
            }

            List<Run> result = new List<Run>();
            // What color to start?
            bool markThisRun = map[0];
            int runpos = 0;
            int pos = 0;
            while (++pos < content.Length) {
                bool thisMarked = map[pos];
                if (thisMarked != markThisRun) {
                    string runText = content.Substring(runpos, pos - runpos);
                    Run newrun = new Run(runText);
                    if (markThisRun ||
                        // We can sometimes get an unmarked punctuation sequence between two marked text sequences.
                        // It's a bit inefficient to have consecutive runs the same color, but it's rare.
                        !Regex.IsMatch(runText, @"\w")) {
                        newrun.Background = highlighter;
                    }
                    else {
                        // Inspired by copyleaks.com: lighter color if it's just one unpunctuated word
                        if (Regex.IsMatch(runText, @"^\s*\w+\s*$")) {
                            newrun.Background = mediumlighter;
                        }
                    }
                    result.Add(newrun);
                    runpos = pos;
                    markThisRun = thisMarked;
                }
            }

            Run finalrun = new Run(content.Substring(runpos, pos - runpos));
            if (markThisRun) finalrun.Background = highlighter;
            result.Add(finalrun);
            return result;
        }
    }
}
