using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Copyvios
{
    // A single word
    public class Word
    {
        private static readonly string[] commonWords = new[] {
            "a", "an", "the", "in", "on",
            "it", "at", "to", "he", "she",
            "as", "is", "was", "are", "of",
            "by", "for", "or", "and", "s" };    // s acts as a word if preceded by apostrophe
        private static readonly HashSet<int> commonHashes = new HashSet<int>(commonWords.Select(w => w.GetHashCode()));

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

    // Shadow a Windows Controls Run
    public enum RunBG
    {
        None,
        Highlight,
        Mediumlight
    }

    public class Sequence
    {
        public string text;
        public RunBG background;

        public Sequence(string t)
        {
            text = t;
            background = RunBG.None;
        }
    }

    public static class Matcher
    {
        const int minGram = 3;
        const int maxGram = 5;

        const Int64 guardhash = 0;

        static readonly Regex splitRE = new Regex(@"\w+");

        // Produce an array of words as their hashes
        static Word[] WordReduce(string text)
        {
            MatchCollection matches = splitRE.Matches(text.ToLower());
            // At this point we know the required array capacity
            Word[] result = new Word[matches.Count + maxGram - minGram];
            int arrind = 0;
            foreach (Match m in matches) {
                result[arrind++] = new Word(m.Index, m.Length, m.Value);
            }

            // Some guards that should work with the above logic
            for (int i = minGram; i < maxGram; i++) {
                result[arrind++] = new Word(text.Length, 0, "a");
            }
            return result;
        }

        // Produce a list of nGrams
        public static List<Chunk> Reducer(string text)
        {
            Word[] wordlist = WordReduce(text);
            List<Chunk> result = new List<Chunk>(wordlist.Length * 2);  // This is a small overestimate of capacity

            // Work through word sequences, ending maxGram away from the end of the list
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
                // These algorithms can occasionally end up as empty ngrams (newgram=0) but that's very rare.
                bool sig = false;
                for (int j = 0; j < 4; j++) {
                    thisword = wordlist[i + j];
                    newgram = (newgram << 8) ^ (UInt32)thisword.Hash;
                    if (!thisword.IsCommon) sig = true;
                }
                if (sig && newgram != 0) {
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
                if (newgram != 0) {
                    result.Add(new Chunk(start, thisword.StartPos - start + thisword.Length, newgram));
                }
            }

            result.Add(new Chunk(text.Length, 0, guardhash));   // Guard a possible edge case
            return result;
        }

        // Match and mark both sides. Using a more efficient lookup with a dictionary of lists could make this
        // O(n) instead of O(n^2), but this simpler search is rarely expensive compared with web access, and it
        // seemed that the structures needed to support O(n) were expensive themselves.
        // We trust the thread pool will use the cores effectively.
        // Experiments show that using half the cores is pretty optimal in runtime
        // There may be occasionally contention when setting isMatch, but it can only ever be setting true.
        public static void Marker(IList<Chunk> wpchunks, IList<Chunk> ebchunks)
        {
            int slices = Environment.ProcessorCount / 2;
            if (slices == 0)    // One processor?
                slices = 1;
            int chunksize = wpchunks.Count / slices;

            Task[] subtasks = new Task[slices];
            for (int i = 0; i < slices; i++) {
                int start = chunksize * i;
                subtasks[i] = Task.Run(() => MarkSubset(wpchunks, ebchunks, start,
                i == slices - 1 ? wpchunks.Count : start + chunksize));
            }

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

        // Provide a sequence of Run-equivalents
        private readonly static Regex wordRE = new Regex(@"\w");
        private readonly static Regex spacewordRE = new Regex(@"^\s*\w+\s*$");

        internal static IEnumerable<Sequence> Markup(string content, bool[] map)
        {
            // I think we already eliminated this, but to be sure...
            if (content.Length == 0) {
                yield break;
            }

            // What color to start?
            bool markThisRun = map[0];
            int runpos = 0;
            int pos = 0;
            while (++pos < content.Length) {
                bool thisMarked = map[pos];
                if (thisMarked != markThisRun) {
                    string runText = content.Substring(runpos, pos - runpos);
                    Sequence newrun = new Sequence(runText);
                    if (markThisRun ||
                        // We can sometimes get an unmarked punctuation sequence between two marked text sequences.
                        // It's a bit inefficient to have consecutive runs the same color, but it's rare.
                        !wordRE.IsMatch(runText)) {
                        newrun.background = RunBG.Highlight;
                    }
                    else {
                        // Inspired by copyleaks.com: lighter color if it's just one unpunctuated word
                        if (spacewordRE.IsMatch(runText)) {
                            newrun.background = RunBG.Mediumlight;
                        }
                    }
                    yield return newrun;
                    runpos = pos;
                    markThisRun = thisMarked;
                }
            }

            Sequence finalrun = new Sequence(content.Substring(runpos, pos - runpos));
            if (markThisRun) finalrun.background = RunBG.Highlight;
            yield return finalrun;
        }
    }
}
