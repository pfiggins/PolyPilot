const fs = require('fs');
const filePath = 'PolyPilot/Components/Pages/Dashboard.razor';
let content = fs.readFileSync(filePath, 'utf8');
const NL = content.includes('\r\n') ? '\r\n' : '\n';

const oldMethod = [
    '    private void SetMaxIterations(string groupId, ChangeEventArgs e)',
    '    {',
    '        if (int.TryParse(e.Value?.ToString(), out var val) && val >= 1)',
    '            _groupMaxIterations[groupId] = val;',
    '    }'
].join(NL);

const newMethod = [
    '    private void SetMaxIterations(string groupId, ChangeEventArgs e)',
    '    {',
    '        if (int.TryParse(e.Value?.ToString(), out var val) && val >= 1)',
    '        {',
    '            _groupMaxIterations[groupId] = val;',
    '            ',
    '            // Update the persistent SessionGroup.MaxReflectIterations',
    '            var group = CopilotService.Organization.Groups.FirstOrDefault(g => g.Id == groupId);',
    '            if (group != null)',
    '            {',
    '                group.MaxReflectIterations = val;',
    '                CopilotService.Organization.SaveOrganization();',
    '                CopilotService.Organization.FlushSaveOrganization();',
    '            }',
    '            ',
    '            // Trigger UI refresh to sync both textboxes',
    '            StateHasChanged();',
    '        }',
    '    }'
].join(NL);

if (content.includes(oldMethod)) {
    content = content.replace(oldMethod, newMethod);
    console.log('SetMaxIterations updated');
} else {
    console.log('ERROR: oldMethod not found');
    process.exit(1);
}

fs.writeFileSync(filePath, content, 'utf8');
console.log('File written successfully');
