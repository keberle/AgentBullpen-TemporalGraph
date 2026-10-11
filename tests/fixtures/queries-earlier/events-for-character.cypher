// Which events was this character part of, in the order the reader learns them?
// Parameters: $character, $pos, $spoilers.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (: the graph highlight).
MATCH (c:Character)-[p:PARTICIPATED_IN]->(e:Event) WHERE $character IN c.aliases AND ($spoilers OR p.revealedPos <= $pos) RETURN e.text AS event, p.storyFrom AS storyTime, p.revealedPos AS revealed, id(e) AS eventId, id(p) AS edgeId ORDER BY p.revealedPos
