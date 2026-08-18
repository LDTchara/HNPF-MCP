// 临时诊断 v2：对比 spawn 参数（去掉 detached / windowsHide / ignore）
const { spawn } = require('child_process');
const ps = 'Start-Process -FilePath "D:\\Game\\Hacknet+DLC+Pathfinder\\Hacknet.exe" -ArgumentList @("-enabledebug","-enablefc") -WorkingDirectory "D:\\Game\\Hacknet+DLC+Pathfinder" -PassThru | Out-Null';
console.log('[diag] variant A: detached=false, windowsHide=false, stdio pipe');
const c = spawn('powershell.exe', ['-NoProfile', '-Command', ps], { stdio: ['ignore', 'pipe', 'pipe'] });
c.stdout?.on('data', (d) => process.stdout.write('[ps-out] ' + d));
c.stderr?.on('data', (d) => process.stdout.write('[ps-err] ' + d));
c.on('error', (e) => console.log('[diag] spawn error:', e.message));
c.on('close', (code) => console.log('[diag] powershell closed code', code));
console.log('[diag] spawned powershell pid', c.pid);
