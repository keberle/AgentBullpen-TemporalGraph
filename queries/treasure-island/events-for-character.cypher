// Which events was this character part of, in the order the reader learns them?
// Parameters: $character, $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (c:Character)-[p:PARTICIPATED_IN]->(e:Event) WHERE $character IN c.aliases AND visible(c) AND visible(p) AND visible(e)
RETURN e.summary AS event, e.storyFrom AS storyTime, e.revealedPos AS revealed, id(e) AS eventId, id(p) AS edgeId ORDER BY e.revealedPos
