# Ranking command syntax by its case (#3)

The third ranking change, and the one aimed at what #3 had left: five colon-delimited commands
outside the first ten, among them `:WAVeform:SOURce` at rank 92.

10 Oct 2026, the library as read again from its originals (632 documents).

## Why they ranked low

The 54845A's syntax diagrams never print the command whole. Page 40 draws `:CHANnel` once at the
start of a railroad diagram and `OFFSet` further along it, so the page's text holds the levels in
separate boxes. bm25 then weighs that page against every page that uses the same words, and the
index ignores case. So to bm25, a DG1000Z *user's guide* page about "the modulating waveform source"
matches `:WAVeform:SOURce` as well as the page that defines it, and it says the words more often.

The case is the one sign left. Prose writes `source` or `Source`; only a command reference writes
`SOURce`.

## The change

`RankingBias.Notation`, `--rank-notation`. When the query holds words in mnemonic case (two or
more capitals followed by lower case: `WAVeform`, `SOURce`, `OFFSet`, `DCFifty`), a page whose
text carries every one of them in exactly that case has its score multiplied, as the label and
recovered-text boosts are. Words in capitals (`SKEW`), capitalised words (`Source`) and units
(`MHz`) don't count. A query typed in lower case is a question about the words, and bm25 answers it
as before.

On by default at 1.4.

## Measured

**Ground truth**, the 33 strings from the 54845A ([before](ground-truth-after-fresh-run.md),
[after](ground-truth-with-notation.md)):

| `--rank-notation` | found | first 25 | first ten |
|---|---|---|---|
| 1.0 (off) | 33 | 31 | 27 |
| 1.2 | 33 | **33** | **32** |
| 1.4, 2.0, 3.0 | 33 | 33 | 32 |

A plateau from 1.2 to 3.0: this switches a behaviour on rather than fitting a number.

| Query | off | on |
|---|---|---|
| `:CHANnel<N>:INPut` | 21 | **1** |
| `:CHANnel<N>:PROBe` | 21 | **1** |
| `:CHANnel<N>:DISPlay` | 26 | **3** |
| `:CHANnel<N>:OFFSet` | 17 | **3** |
| `:WAVeform:SOURce` | 92 | **8** |
| `The EXTernal command is only available on the 54810/20` | 13 | 13 |

The one left is a sentence, not a command.

**Commands from other manuals.** The strings above were chosen because they failed, so they
flatter anything aimed at them. The two prose controls can't see this change: none of the 40
ordinary-page phrases or 60 bare words holds a word in mnemonic case, so "unchanged" there would
prove nothing. A new control asks the shape the change touches.
`tools/measure-command-pages.ps1` draws 60 pages at random from every manual but the 54845A, takes
a colon-delimited command printed on each, and asks for it exactly as printed
([off](command-pages-without-notation.md), [on](command-pages-with-notation.md)):

| | off | on |
|---|---|---|
| That page first | 18 | 19 |
| That page in the first ten | 25 | **31** |
| That page found at all | 41 | **47** |
| Its manual in the first ten | 36 | **39** |

Page by page: 21 better, **none worse**. One manual fell back: for `:MEMory:DATA` the E4438C *User
Guide* went from 8 to 44. The pages that rose above it are the E4438C *SCPI Command Reference*,
which defines the command. The User Guide only mentions it.

**The rest.** The ordinary-pages and bare-term controls are unchanged (26 first and 38 in the first
ten; 6 and 9), as they must be, since neither asks a query the change acts on. On the 25 repaired
pages, not one rank moved.

## Known

A plural acronym, `LEDs`, has the same shape as a mnemonic, and so does `UNITs`, which is one. It is
kept: all it does is favour pages that spell the word the way it was typed.
