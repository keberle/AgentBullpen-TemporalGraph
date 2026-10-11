// Facts whose text, or the name of something they are about, contains a term.
// Parameters: $term, $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (f:Fact) WHERE visible(f)
OPTIONAL MATCH (x) WHERE id(x) IN f.subject AND toLower(x.name) CONTAINS toLower($term)
WITH f, count(x) AS named WHERE named > 0 OR toLower(f.text) CONTAINS toLower($term)
OPTIONAL MATCH (o) WHERE id(o) IN f.subject
WITH f, collect(o.name) AS about, collect(id(o)) AS aboutIds
RETURN f.text AS fact, about, f.revealedPos AS revealed, id(f) AS factId, aboutIds ORDER BY f.revealedPos
