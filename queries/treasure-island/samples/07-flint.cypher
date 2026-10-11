// What is known about Flint?
// Facts about Captain Flint (alias 'Flint'), and how each was discovered.
// Parameters: $pos (the timeline's chapter end), $spoilers. visible(x) is the spoiler rule on a node or an edge.
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids), hidden in the results grid.
MATCH (c) WHERE 'Flint' IN c.aliases AND visible(c)
MATCH (e:Event)-[d:DISCOVERED]->(f:Fact) WHERE id(c) IN f.subject AND visible(f) AND visible(d) AND visible(e)
RETURN f.text AS fact, d.how AS how, e.summary AS event, d.revealedPos AS revealed, id(f) AS factId, id(e) AS eventId, id(d) AS edgeId ORDER BY d.revealedPos
