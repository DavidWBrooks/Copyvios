﻿using System;
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

    // A single nGram
    public class Chunk
    {
        public int startpos;   // Position in the original text
        public int len;        // Length in the original text
        public UInt64 hash;
        public bool isMatch;

        public Chunk(int s, int l, UInt64 h)
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

        static readonly string[] smallwords = new[] {
            "a", "an", "the", "in", "on",
            "it", "at", "to", "he", "she",
            "as", "is", "was", "are", "of",
            "by", "for", "or", "and", "s" };    // s seems to be a word if preceded by apostrophe
        static readonly int[] smallhashes = smallwords.Select(w => w.GetHashCode()).ToArray();
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
                int start = thisword.startpos;
                UInt64 newgram = 0;

                // After some experimentation with several different algorithms, we seem to get the best results
                // by combining two approaches, which also have few false positives:
                // Combine 4 consecutive words, if at least one is significant
                // Combine 3 significant words, skipping as many common words as necessary
                bool sig = false;
                for (int j = 0; j < 4; j++) {
                    thisword = wordlist[i + j];
                    int h = thisword.hash;
                    newgram = (newgram << 8) ^ (UInt32)h;
                    if (!smallhashes.Contains(h)) sig = true;
                }
                if (sig && newgram != 0) {  // Although newgram can theoretically be 0, testing shows it very rare.
                    result.Add(new Chunk(start, thisword.startpos - start + thisword.len, newgram));
                }

                newgram = 0;
                int wordsingram = 0;
                for (int j = i; j < wordlist.Length; j++) {
                    thisword = wordlist[j];
                    int h = thisword.hash;
                    if (!smallhashes.Contains(h)) {
                        newgram = (newgram << 8) ^ (UInt32)h;
                        if (++wordsingram == minGram)
                            break;
                    }
                }
                if (newgram != 0) {     // Although this can theoretically be 0, testing shows it very rare.
                    result.Add(new Chunk(start, thisword.startpos - start + thisword.len, newgram));
                }
            }

            result.Add(new Chunk(text.Length, 0, guardhash));   // Guard a possible edge case
            return result;
        }

        // Match and mark both sides. Using a more efficient lookup with a dictionary of lists would make this
        // O(n) instead of O(n^2), but this simpler search is rarely expensive compared with web access.
        // Using a 2x parallelization helps (on a 2-core box) but only by about 20% even on a large page.
        public static void Marker(IList<Chunk> wpchunks, IList<Chunk> ebchunks)
        {
            Task[] tasks = new Task[2];
            int half = wpchunks.Count / 2;
            tasks[0] = Task.Run(() => MarkSubset(wpchunks, ebchunks, 0, half));
            tasks[1] = Task.Run(() => MarkSubset(wpchunks, ebchunks, half, wpchunks.Count));

            Task.WaitAll(tasks);
        }

        private static void MarkSubset(IList<Chunk> wpchunks, IList<Chunk> ebchunks, int startind, int end)
        {
            for (int i = startind; i < end; i++) {
                Chunk wpchunk = wpchunks[i];
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
            foreach (Chunk chunk in chunks.Where(c => c.isMatch)) {
                int ind = chunk.startpos;
                int length = chunk.len;
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
