// Which characters has the reader met by this point?
// Parameters: $pos (a text position; the chapter slider and query --chapter N set it to the end of chapter N).
// Columns ending in Id/Ids carry the node ids of the name columns, for the chat tools (which strip them).
MATCH (c:Character) WHERE c.evidenceStart < $pos RETURN c.name AS name, id(c) AS nameId ORDER BY c.evidenceStart
