// events_of(character, pos): a character's events in full: what happened, narrated or only referenced, the character's role, who else took part.
// Parameters: $character, $pos, $spoilers.
// visible(x) is the spoiler rule on a node or an edge (Spoilers off: revealed by $pos and not retracted; Spoilers on: true in story time by story($pos)).
// Columns ending in Id/Ids carry node ids (and edgeId/edgeIds the edge ids) for the chat tools, which strip them (the graph highlight).
MATCH (c:Character)-[p:PARTICIPATED_IN]->(e:Event) WHERE $character IN c.aliases AND visible(c) AND visible(p) AND visible(e)
OPTIONAL MATCH (o:Character)-[q:PARTICIPATED_IN]->(e) WHERE visible(q) AND visible(o)
WITH e, p, collect(o.name) AS others, collect(id(o)) AS othersIds
RETURN e.summary AS event, e.kind AS kind, p.role AS role, others, e.storyFrom AS storyTime, e.revealedPos AS revealed, id(e) AS eventId, othersIds, id(p) AS edgeId
ORDER BY e.revealedPos
