// Whom has d’Artagnan met so far?
// Events d’Artagnan (alias 'D’Artagnan') took part in with someone else. A meeting is an Event with two participants.
// Parameters: $pos (the timeline's chapter end), $spoilers. visible(x) is the spoiler rule on a node or an edge.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids), hidden in the results grid.
MATCH (a:Character)-[p:PARTICIPATED_IN]->(e:Event)<-[q:PARTICIPATED_IN]-(b:Character)
WHERE 'D’Artagnan' IN a.aliases AND visible(a) AND visible(p) AND visible(e) AND visible(q) AND visible(b)
WITH b, min(e.revealedPos) AS revealed, collect(id(p)) AS edgeIds
RETURN b.name AS met, revealed, id(b) AS metId, edgeIds ORDER BY revealed
