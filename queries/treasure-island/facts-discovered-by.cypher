// facts_discovered_by(character, pos): the facts a character came to, through which event and how (witnessed, told, overheard, inferred, read, recalled).
// Parameters: $character, $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (c:Character)-[p:PARTICIPATED_IN]->(e:Event)-[d:DISCOVERED]->(f:Fact)
WHERE $character IN c.aliases AND visible(c) AND visible(p) AND visible(e) AND visible(d) AND visible(f)
RETURN f.text AS fact, d.how AS how, e.summary AS event, d.revealedPos AS revealed, id(f) AS factId, id(e) AS eventId, id(d) AS edgeId ORDER BY d.revealedPos
