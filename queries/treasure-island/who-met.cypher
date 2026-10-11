// Whom has this character met? A meeting is an Event both took part in (there is no MET edge).
// Parameters: $character (a name the character goes by, e.g. Jim), $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (a:Character)-[p:PARTICIPATED_IN]->(e:Event)<-[q:PARTICIPATED_IN]-(b:Character)
WHERE $character IN a.aliases AND visible(a) AND visible(p) AND visible(e) AND visible(q) AND visible(b)
WITH b, min(e.revealedPos) AS revealed, collect(id(p)) AS edgeIds
RETURN b.name AS met, id(b) AS metId, edgeIds ORDER BY revealed
