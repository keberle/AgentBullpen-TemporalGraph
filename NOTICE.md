# Notice

The code in this repository is under the [MIT License](LICENSE). Some files are not code, or come from elsewhere, and carry their own terms:

## Book texts

`data/books/source/` holds three Project Gutenberg eBooks, each kept exactly as downloaded, with its Project Gutenberg header and licence at the top and bottom of the file:

| File | eBook | Author |
|---|---|---|
| `hound-of-the-baskervilles.txt` | [#2852](https://www.gutenberg.org/ebooks/2852) *The Hound of the Baskervilles* | Arthur Conan Doyle |
| `three-musketeers.txt` | [#1257](https://www.gutenberg.org/ebooks/1257) *The Three Musketeers* | Alexandre Dumas |
| `treasure-island.txt` | [#120](https://www.gutenberg.org/ebooks/120) *Treasure Island* | Robert Louis Stevenson |

These works are in the public domain in the United States; check the laws of your country before using them elsewhere. The Project Gutenberg licence included in each file governs the use of the Project Gutenberg name and trademark. The book databases in `data/books/*.sqlite` are derived from these texts (quotes and offsets into them).

## Images

The book hero images (`web/public/books/<id>.webp`) and the character avatars (`web/public/books/<id>/characters/`) were generated for this project with OpenAI's GPT-Image-2.5 and converted to WebP. OpenAI's Services Agreement (§4.1) assigns ownership of the output to the customer. The README screenshot (`media/`) is of this app.

## Third-party data and code

| What | Where | Terms |
|---|---|---|
| **LitBank** annotations of *The Hound of the Baskervilles* (David Bamman et al.) | `data/crosscheck/litbank/` | [CC BY 4.0](https://creativecommons.org/licenses/by/4.0/); attribution in that folder's README |
| **BookNLP** output for *The Hound of the Baskervilles* (the tool: David Bamman, MIT) | `data/crosscheck/booknlp/` | produced by running the MIT-licensed tool on the public-domain text; see that folder's README |
| **openCypher** grammar (© Neo Technology / the openCypher community) | `src/TemporalGraph.Query/grammar/Cypher.g4`, and the parser generated from it in `src/TemporalGraph.Query/Generated/` | [Apache License 2.0](https://www.apache.org/licenses/LICENSE-2.0); the grammar's header and that folder's README |

Packages restored by NuGet and npm (for example the ANTLR runtime, Microsoft Agent Framework, react-force-graph, Fluent UI and the Open Sans and JetBrains Mono fonts) are not in this repository and come under their own licences.
