// What is known about a character, with everything each fact is about, in the order it was learned.
// Parameters: $character, $pos, $spoilers.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (: the graph highlight).
MATCH (f:Fact)-[r:ABOUT]->(c) WHERE $character IN c.aliases AND ($spoilers OR r.revealedPos <= $pos)
OPTIONAL MATCH (f)-[:ABOUT]->(o) WITH f, r, collect(o.name) AS about, collect(id(o)) AS aboutIds
RETURN f.text AS fact, about, r.revealedPos AS revealed, aboutIds, id(f) AS factId, id(r) AS edgeId ORDER BY r.revealedPos
