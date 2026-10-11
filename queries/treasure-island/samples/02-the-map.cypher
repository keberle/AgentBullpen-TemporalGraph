// Everything the record says about the map
// Facts whose text contains 'map', with what each is about.
// Parameters: $pos (the timeline's chapter end), $spoilers. visible(x) is the spoiler rule on a node or an edge.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids), hidden in the results grid.
MATCH (f:Fact) WHERE visible(f) AND toLower(f.text) CONTAINS 'map'
OPTIONAL MATCH (o) WHERE id(o) IN f.subject
WITH f, collect(o.name) AS about, collect(id(o)) AS aboutIds
RETURN f.text AS fact, about, f.revealedPos AS revealed, id(f) AS factId, aboutIds ORDER BY f.revealedPos
