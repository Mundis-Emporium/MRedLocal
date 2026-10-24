local root = assert(os.getenv('VORP_CHARACTER_ROOT'), 'VORP_CHARACTER_ROOT is required')
local file = assert(io.open(root .. '/server/server.lua', 'rb'))
local sourceText = file:read('*a')
file:close()
local first = assert(sourceText:find('local charSelected <const> = {}', 1, true))
local last = assert(sourceText:find('RegisterNetEvent("vorpcharacter:setPlayerCompChange"', first, true))
local chunk = assert(load(sourceText:sub(first, last - 1), 'character selection handlers'))
local handlers = {}
local selected = 0
Core = { getUser = function()
    return { setUsedCharacter = function() selected = selected + 1 end }
end }
function RegisterServerEvent(name, fn) handlers[name] = fn end
function AddEventHandler(name, fn) handlers[name] = fn end
chunk()
source = 7
handlers.vorp_CharSelectedCharacter(101)
handlers.vorp_CharSelectedCharacter(101)
assert(selected == 1, 'one selection per connection')
handlers.playerDropped()
handlers.vorp_CharSelectedCharacter(101)
assert(selected == 2, 'reused source can select after disconnect')
print('character source reuse test passed')
