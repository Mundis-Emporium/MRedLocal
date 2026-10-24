local root = assert(os.getenv('VORP_TEST_ROOT'), 'VORP_TEST_ROOT is required') .. '/'
local alias = 'localtest:secondary'
local captured

GlobalState = {}
Config = {
    initGroup = 'user', initJob = 'unemployed', initJobGrade = 0,
    initJobLabel = 'Unemployed', initMoney = 0, initGold = 0,
    initRol = 0, initXp = 0, initInvCapacity = 35,
}
function GetPlayerName() return 'Synthetic' end
function TriggerClientEvent() end
function TriggerEvent() end
function Player()
    return { state = { set = function() end } }
end
function Character(info)
    captured = info
    local id = -1
    return {
        SaveNewCharacterInDb = function(callback) callback(101) end,
        CharIdentifier = function(value)
            if value then id = value end
            return id
        end,
        updateCharUi = function() end,
        getCharacter = function() return info end,
    }
end

dofile(root .. 'server/class/user.lua')
local user = User(2, alias, 'user', 0, 'license:abcdef1234567890', 1, 1)
user.addCharacter({ firstname = 'New', lastname = 'Tester', gender = 'Male', age = 30,
    charDescription = '', nickname = '', skin = '{}', comps = '{}', compTints = '{}' })
assert(captured.identifier == alias, 'normal creator must save under alias identifier')
assert(captured.inventory == '{}', 'normal creator starts with empty inventory')
assert(captured.money == 0 and captured.gold == 0, 'normal creator starts with configured zero currency')
assert(user.usedCharacterId == 101, 'normal creator selected its new character')
print('normal VORP character creator identity test passed')
