using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows.Documents;
using System.Windows.Media;

namespace Copyvios
{
    // A single word
    public class Word
    {
        public int startpos;
        public int len;
        public int hash;
        public Word(int s, int l, string word)
        {
            startpos = s;
            len = l;
            hash = word.GetHashCode();
        }
    }

    // a single nGram
    public class Chunk
    {
        public int startpos;   // Position in the original text
        public int len;        // Length in the original text
        public Int64 hash;
        public bool isMatch;

        public Chunk(int s, int l, Int64 h)
        {
            startpos = s;
            len = l;
            hash = h;
            isMatch = false;
        }
    }

    public static class Matcher
    {
        const int minGram = 3;
        const int maxGram = 5;

        static readonly string[] smallwords = new[] { "a", "an", "the", "in", "it", "at", "to", "he", "she", "as", "is", "of" };
        static readonly int[] smallhashes = smallwords.Select(w => w.GetHashCode()).ToArray();
        const Int64 guardhash = 0;
        static readonly Brush highlighter = new SolidColorBrush(Color.FromRgb(0xFF, 0xAA, 0xAA));

        // Do a quadratic match (it's OK, I'll wait) on the chunks
        public static void Marker(List<Chunk> wpchunks, List<Chunk> ebchunks)
        {
            foreach (Chunk wpchunk in wpchunks) {
                foreach (Chunk ebchunk in ebchunks) {
                    if (wpchunk.hash == ebchunk.hash) {
                        wpchunk.isMatch = ebchunk.isMatch = true;
                    }
                }
            }
        }

        // Consolidate chunks into an array of on/off settings
        public static void Mapper(List<Chunk> chunks, bool[] bitmap)
        {
            foreach(Chunk chunk in chunks.Where(c => c.isMatch)) {
                int ind = chunk.startpos;
                int length = chunk.len;
                while (length-- > 0) {
                    bitmap[ind++] = true;
                }
            }
        }

        // Produce a list of nGrams
        public static List<Chunk> Reducer(string text)
        {
            Word[] wordlist = WordReduce(text);
            List<Chunk> result = new List<Chunk>(wordlist.Length + 1);

            // Work through word sequences, ending 3 away from the end of the list
            // Yes, adjacent grams will have overlapping words.

            for (int i = 0; i <= (wordlist.Length - maxGram); i++) {
                if (smallhashes.Contains(wordlist[i].hash)) continue;   // Dom't start on a common word
                int start = wordlist[i].startpos;
                Int64 newgram = 0;
                int gramlength = 0;
                int wordsingram = 0;
                for (int j = 0; j < maxGram; j++) {
                    Word thisword = wordlist[i + j];
                    int h = thisword.hash;
                    if (!smallhashes.Contains(h)) {
                        // If you don't shift-left, the words can be in any order. This is why I'm using Int64.
                        // But forget the shift if you actually want the "unordered" logic.
                        newgram = (newgram << 8) ^ h;
                        gramlength = thisword.startpos - start + thisword.len;
                        if (++wordsingram == minGram)
                            break;
                    }
                }

                if (newgram != 0) {
                    result.Add(new Chunk(start, gramlength, newgram));
                }
            }

            result.Add(new Chunk(text.Length, 0, guardhash));   // Guard
            return result;
        }

        // Produce a list of words as hashes
        static Word[] WordReduce(string text)
        {
            MatchCollection matches = Regex.Matches(text.ToLower(), @"[-\w]+");
            List<Word> result = new List<Word>(matches.Count);
            foreach (Match m in matches) {
                result.Add(new Word(m.Index, m.Length, m.Value.Replace("-", "")));
            }

            // Some guards that should work with the above logic
            for (int i = minGram; i < maxGram; i++) {
                result.Add(new Word(text.Length, 0, "a"));
            }
            return result.ToArray();
        }

        // Provide a sequence of Runs
        internal static IEnumerable<Run> Markup(string content, bool[] map)
        {
            // I think we already eliminated this, but to be sure...
            if (content.Length == 0) {
                yield break;
            }

            // What color to start?
            bool runMarked = map[0];
            int runpos = 0;
            int pos = 0;
            while (++pos < content.Length) {
                bool thisMarked = map[pos];
                if (thisMarked != runMarked) {
                    Run newrun = new Run(content.Substring(runpos, pos - runpos));
                    if (runMarked) newrun.Background = highlighter;
                    yield return newrun;
                    runpos = pos;
                    runMarked = thisMarked;
                }
            }

            Run finalrun = new Run(content.Substring(runpos, pos - runpos));
            if (runMarked) finalrun.Background = highlighter;
            yield return finalrun;
        }
    }
}
