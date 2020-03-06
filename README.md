# Copyvios
Client-side replacement for Wikipedia's Copyvio detector for URLs

Build with Visual Studio 2017 or greater. The executable runs on Windows, and needs Framework 4.5 or better. Start it and enter (a) a Wikipedia article name and (b) another URL. You can put the two parameters on the command line for easier scripting.

This is a clean-room implementation without reference to any advanced language analysis or efficient text-matching logic (i.e. no Myers algorithm) but seems pretty close to what the wmflabs tool does, in this limited scenario. It only takes a second or two; most of the time is spent downloading the two documents. I wrote this for myself, and the language processing for the URL is designed only for a certain class of Wikisource articles. Other types of document will be ugly. That's probably the area most in need of improvement.


Note for the technically-advanced: yes, I did originally try to make it do the Right Thing and execute asynchronously and interruptible, but ran into a strange thread-conflict exception. There are left-over traces of that code in this synchronous version. The raw processing turned out not to take as long as I anticipated.

Theory of operation (along with a few practical tweaks):
*Load two documents and reduce them to plain text
*Reduce to a list of hashes of (alphanumeric) words
*Combine word hashes, between 3 and 5 at a time, into grams
*Look for matching grams between the sources
*Use the extents of the matches to color the plain text.
