// Facts and events whose text, or the name of something they are about, contains a term.
// Parameters: $term, $pos, $spoilers.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (: the graph highlight).
MATCH (f)-[r:ABOUT]->(x) WHERE (f:Fact OR f:Event) AND ($spoilers OR r.revealedPos <= $pos)
  AND (toLower(f.text) CONTAINS toLower($term) OR toLower(x.name) CONTAINS toLower($term))
WITH f, min(r.revealedPos) AS revealed
OPTIONAL MATCH (f)-[a:ABOUT]->(o) WITH f, revealed, collect(o.name) AS about, collect(id(o)) AS aboutIds, collect(id(a)) AS edgeIds
RETURN f.text AS fact, about, revealed, id(f) AS factId, aboutIds, edgeIds ORDER BY revealed
