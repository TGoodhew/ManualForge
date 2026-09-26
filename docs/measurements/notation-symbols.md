# How often does electronic notation survive recognition?

Prompted by a real error found by eye on 25 Sep: `461A` p40 renders **33 ohms ±10%** as
`33 ohrs v/10%`, and one line later renders the same `±` correctly as `+/-`. The question was
whether that is one bad scan or a corpus-wide hole.

Measured over all 632 indexed documents, 195.6 million characters of extracted text.

## The symbols are mostly there

| Symbol | Occurrences |
|---|---|
| `±` U+00B1 | 50,445 |
| `°` U+00B0 | 12,830 |
| `μ` U+03BC / `µ` U+00B5 | 13,498 |
| `Ω` U+03A9 / U+2126 | 9,905 |

## The `±` → `v` corruption is rare and concentrated

Searching for the unambiguous nonsense pattern — a letter followed by a slash where a tolerance
belongs, `v/10%` — finds **62 occurrences across 20 of 630 documents**, against 50,445 correct
`±`. That is 0.1% of the 127,796 places a percentage appears.

It is also concentrated: the worst ten documents hold **84%** of it.

| Document | corrupted | correct `±` |
|---|---|---|
| `HP_5347A_Service_Manual` | 7 | 183 |
| `4725A` | 4 | 419 |
| `331A-332A._OSM` | 4 | 0 |
| **`461A`** | **3** | **4** |
| `08349-90239-opr` | 1 | 490 |

`461A` is the outlier and explains how this was found: three corruptions against four correct
instances is a document where the symbol is as likely to be wrong as right. `HP_5347A` at 7 against
183, or `08349-90239-opr` at 1 against 490, are documents where it is essentially always right.

**So: one bad scan, not a corpus-wide hole.** The fix for `461A` is a better scan or a re-OCR of
that document, not a change to the recogniser.

## What could not be measured this way, and the mistake worth recording

The obvious next step was to count the same way for `Ω`, `°` and `µ`. The first attempt produced
6,861 apparent `Ω`→`Q` corruptions and 13,800 apparent `°`→`0` corruptions, which would have been
the headline of this document.

**Both numbers were almost entirely false positives**, and only sampling the matching lines showed
it:

| Matched as a corruption | Actually |
|---|---|
| `8594Q` | a model number |
| `TDS 500D, TDS 600C` | model numbers |
| `15-16 Theory of Operation HP 8719C` | a page reference |
| `TC=0+100 F` | a temperature coefficient, a genuine zero |

A handful were real — `3 1 MQ A2AE1R1 2M RESISTOR` is `MΩ` — but nothing like the counts suggested.

The `±` case worked only because `v/10%` is *unambiguous nonsense*: no legitimate text puts a bare
letter and a slash in front of a tolerance. `Q` after a digit, and `0` before `C`, are both common
in legitimate text — model numbers, part numbers, page references, real zeros. There is no pattern
that separates the corruption from the legitimate use **without knowing what the page actually
says**.

Which is the argument for ground truth, made from the other direction: this is precisely the
measurement that cannot be taken from the output alone. A corpus can tell you a symbol is rare; it
cannot tell you a symbol is wrong.

## Reproducing

The survey reads a sidecar dump (`index --sidecars`) rather than the index, because it needs raw
extracted text per document. No script is committed for it: the useful part was the discipline of
sampling the matches, not the regexes, and a committed script would invite someone to trust the
next set of counts without doing that.
