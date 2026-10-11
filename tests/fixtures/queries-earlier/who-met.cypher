// Whom has this character met, as revealed so far?
// Parameters: $character (a name the character goes by, e.g. Holmes), $pos, $spoilers (true: don't hide what the reader hasn't reached).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (: the graph highlight).
MATCH (a:Character)-[m:MET]-(b:Character) WHERE $character IN a.aliases AND ($spoilers OR m.revealedPos <= $pos) WITH b, collect(id(m)) AS edgeIds RETURN b.name AS met, id(b) AS metId, edgeIds
