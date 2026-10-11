// What is known about a person, place or thing, with everything each fact is about, in the order it was learned.
// A Fact is a node; what it is about is its subject property (node ids). There is no ABOUT edge.
// Parameters: $character, $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (c) WHERE $character IN c.aliases AND visible(c)
MATCH (f:Fact) WHERE id(c) IN f.subject AND visible(f)
OPTIONAL MATCH (o) WHERE id(o) IN f.subject
WITH f, collect(o.name) AS about, collect(id(o)) AS aboutIds
RETURN f.text AS fact, about, f.revealedPos AS revealed, aboutIds, id(f) AS factId ORDER BY f.revealedPos
