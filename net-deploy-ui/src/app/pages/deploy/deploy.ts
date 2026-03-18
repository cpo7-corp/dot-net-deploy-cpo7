import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule, TranslateService } from '@ngx-translate/core';
import { ServicesMonitorService } from '../../services/services-monitor.service';
import { DeployService } from '../../services/deploy.service';
import { SettingsService } from '../../services/settings.service';
import { ServiceStatus, DeployLogEntry, VpsSettings } from '../../models/api-models';

@Component({
  selector: 'app-deploy',
  standalone: true,
  imports: [CommonModule, FormsModule, TranslateModule],
  templateUrl: './deploy.html',
  styleUrl: './deploy.less'
})
export class DeployComponent implements OnInit {
  private servicesSvc = inject(ServicesMonitorService);
  private deploySvc = inject(DeployService);
  private settingsSvc = inject(SettingsService);
  private translate = inject(TranslateService);

  services = signal<ServiceStatus[]>([]);
  environments = signal<VpsSettings[]>([]);
  selectedEnvironmentId = signal<string | null>(null);
  selectedServiceIds: Set<string> = new Set();
  serviceBranches: Record<string, string> = {}; // Stores custom branch per service

  loading = signal<boolean>(true);
  deploying = signal<boolean>(false);
  logs = signal<DeployLogEntry[]>([]);
  deploymentProgress = signal<Record<string, { compiled: string; deployed: string; heartbeat: string; buildTime: string; buildStartTime?: number }>>({});
  elapsedTime = signal<string>('00:00');


  failedServiceIds = signal<string[]>([]);
  deployPull = signal<boolean>(true);
  deployBuild = signal<boolean>(true);
  deployTransfer = signal<boolean>(true);
  
  currentSessionId = signal<string | null>(null);
  isPaused = signal<boolean>(false);
  private timerInterval: any;
  private startTime: number = 0;

  ngOnInit() {
    this.servicesSvc.getAll().subscribe({
      next: (data) => {
        const sorted = data.sort((a, b) => a.name.localeCompare(b.name));
        this.services.set(sorted);
        // Initialize default branch for each service
        data.forEach(s => {
          if (s.id) {
            // Find default branch for current environment if matches
            const envId = this.selectedEnvironmentId();
            const envCfg = s.environments?.find(e => e.environmentId === envId);
            this.serviceBranches[s.id] = envCfg?.defaultBranch || 'main';
          }
        });
        this.loading.set(false);
      }
    });

    this.settingsSvc.getSettings().subscribe(data => {
      this.environments.set(data.vpsEnvironments || []);
      const savedEnvId = localStorage.getItem('lastEnvironmentId');
      const environmentExists = data.vpsEnvironments?.some(e => e.id === savedEnvId);

      if (savedEnvId && environmentExists) {
        this.selectedEnvironmentId.set(savedEnvId);
      } else if (data.vpsEnvironments?.length > 0) {
        this.selectedEnvironmentId.set(data.vpsEnvironments[0].id || null);
      }
      this.updateDefaultBranches();
    });
  }

  onEnvironmentChange(id: string | null) {
    if (id) {
      localStorage.setItem('lastEnvironmentId', id);
    }
    this.updateDefaultBranches();
  }

  private updateDefaultBranches() {
    const envId = this.selectedEnvironmentId();
    this.services().forEach(s => {
      if (s.id) {
        const envCfg = s.environments?.find(e => e.environmentId === envId);
        this.serviceBranches[s.id] = envCfg?.defaultBranch || 'main';
      }
    });
  }

  setServiceBranch(id: string, branch: string) {
    this.serviceBranches[id] = branch;
  }

  toggleService(id: string) {
    if (this.selectedServiceIds.has(id)) {
      this.selectedServiceIds.delete(id);
    } else {
      this.selectedServiceIds.add(id);
    }
  }

  selectAll() {
    this.services().forEach(s => {
      if (s.id) this.selectedServiceIds.add(s.id);
    });
  }

  clearSelection() {
    this.selectedServiceIds.clear();
  }

  startDeploy(configs?: { serviceId: string, branch: string }[], forceClean: boolean = true, retryMode?: number) {
    if (!configs && this.selectedServiceIds.size === 0) return;

    this.deploying.set(true);
    this.logs.set([]);
    this.failedServiceIds.set([]);
    this.deploymentProgress.set({}); // Reset progress
    this.startTimer();

    const deploymentConfigs = configs || Array.from(this.selectedServiceIds).map(id => ({
      serviceId: id,
      branch: this.serviceBranches[id] || 'main'
    }));

    // Initialize progress for each selected service
    const initialProgress: any = {};
    deploymentConfigs.forEach(c => {
      initialProgress[c.serviceId] = { compiled: 'pending', deployed: 'pending', heartbeat: 'pending', buildTime: '' };
    });
    this.deploymentProgress.set(initialProgress);

    const pull = retryMode === 3 ? false : this.deployPull();
    const build = retryMode === 3 ? false : this.deployBuild();
    const deploy = retryMode === 3 ? true : this.deployTransfer();

    this.deploySvc.deploy(deploymentConfigs, this.selectedEnvironmentId(), forceClean, pull, build, deploy).subscribe({
      next: (entry: DeployLogEntry) => {
        if (entry.level === 'SESSION_ID') {
          this.currentSessionId.set(entry.message);
          return;
        }

        this.logs.update(prev => [...prev, entry]);

        if (entry.serviceId) {
          this.updateServiceProgress(entry.serviceId, entry.message, entry.level);
        }

        // Track failed services
        if (entry.level === 'ERROR' && entry.serviceId) {
          this.failedServiceIds.update(fails => [...new Set([...fails, entry.serviceId!])]);
        }

        this.scrollToBottom();
      },

      complete: () => {
        this.addDeploymentSummary();
        this.deploying.set(false);
        this.currentSessionId.set(null);
        this.isPaused.set(false);
        this.stopTimer();
        // Reload services to update status/last deployed date
        this.loading.set(true);
        this.servicesSvc.getAll().subscribe(data => {
          this.services.set(data.sort((a, b) => a.name.localeCompare(b.name)));
          this.loading.set(false);
        });
      },
      error: (err: any) => {
        this.deploying.set(false);
        this.currentSessionId.set(null);
        this.isPaused.set(false);
        this.stopTimer();
        console.error('Deploy error', err);
        this.logs.update(prev => [...prev, {
          sessionId: 'client',
          level: 'ERROR',
          message: 'Connection to server failed or dropped randomly.',
          timestamp: new Date().toISOString()
        }]);
      }
    });
  }

  retryFailed() {
    const failedIds = this.failedServiceIds();
    if (failedIds.length === 0) return;

    const configs = failedIds.map(id => ({
      serviceId: id,
      branch: this.serviceBranches[id] || 'main'
    }));

    // Retry just skips clone and build to purely retry transfer.
    this.startDeploy(configs, false, 3);
  }

  stopDeployment() {
    const sid = this.currentSessionId();
    if (sid) {
      this.deploySvc.stop(sid).subscribe();
    }
  }

  togglePause() {
    const sid = this.currentSessionId();
    if (!sid) return;

    if (this.isPaused()) {
      this.deploySvc.resume(sid).subscribe(() => this.isPaused.set(false));
    } else {
      this.deploySvc.pause(sid).subscribe(() => this.isPaused.set(true));
    }
  }

  updateServiceProgress(serviceId: string, message: string, level: string) {
    const progress = { ...this.deploymentProgress() };
    if (!progress[serviceId]) return;

    const row = { ...progress[serviceId] };

    // Compiled
    if (message.includes('🔨 [Prep] Building')) {
      row.compiled = 'process';
      row.buildStartTime = Date.now();
    }
    if (message.includes('✅ [Prep] Prepared') || message.includes('⏭️ [Prep] Build output already exists') || message.includes('❌ Preparation failed')) {
      if (message.includes('✅ [Prep] Prepared') || message.includes('⏭️ [Prep] Build output already exists')) {
        row.compiled = 'success';
      }
      else {
        row.compiled = 'error';
      }

      if (row.buildStartTime) {
        const duration = ((Date.now() - row.buildStartTime) / 1000).toFixed(1);
        row.buildTime = duration + 's';
      }
    }


    // Deployed
    if (message.includes('🚀 Uploading files') || message.includes('📂 Copying files')) row.deployed = 'process';
    if (message.includes('✅ Files uploaded') || message.includes('✅ Files copied')) row.deployed = 'success';
    if (message.includes('❌ Failed to transfer')) row.deployed = 'error';

    // Heartbeat
    if (message.includes('💓 Checking heartbeat')) row.heartbeat = 'process';
    if (message.includes('✅ Heartbeat OK')) row.heartbeat = 'success';
    if (message.includes('⚠️ Heartbeat returned error')) row.heartbeat = 'error';
    if (message.includes('❌ Heartbeat failed')) row.heartbeat = 'error';

    if (level === 'ERROR') {
      if (row.compiled === 'process' || row.compiled === 'pending') row.compiled = 'error';
      if (row.deployed === 'process' || row.deployed === 'pending') row.deployed = 'error';
      if (row.heartbeat === 'process' || row.heartbeat === 'pending') row.heartbeat = 'error';
    }

    progress[serviceId] = row;
    this.deploymentProgress.set(progress);
  }



  objectKeys(obj: any) { return Object.keys(obj); }
  private addDeploymentSummary() {
    const summaryHeader = this.translate.instant('deploymentSummary');
    const totalTimeLabel = this.translate.instant('totalTime');
    const totalDuration = this.elapsedTime();
    
    this.logs.update(prev => [...prev, 
      { sessionId: 'client', level: 'SUCCESS', message: '========================================================================', timestamp: new Date().toISOString() },
      { sessionId: 'client', level: 'SUCCESS', message: `📊 ${summaryHeader.toUpperCase()}`, timestamp: new Date().toISOString() },
      { sessionId: 'client', level: 'SUCCESS', message: '------------------------------------------------------------------------', timestamp: new Date().toISOString() }
    ]);

    const progress = this.deploymentProgress();
    Object.keys(progress).forEach(id => {
      const name = this.getServiceName(id);
      
      const getIcon = (status: string) => {
        if (status === 'success') return '✅';
        if (status === 'error') return '❌';
        if (status === 'skip') return '⏭️';
        return '⚪';
      };

      const bStatus = getIcon(progress[id].compiled);
      const dStatus = getIcon(progress[id].deployed);
      const hStatus = getIcon(progress[id].heartbeat);
      const bTime = progress[id].buildTime ? `(${progress[id].buildTime})` : '';
      
      // Use fixed width padding for alignment in monospace font
      const namePart = name.padEnd(25);
      const buildPart = `${bStatus} ${bTime}`.padEnd(15);
      const deployPart = `${dStatus}`.padEnd(10);
      const hbPart = `${hStatus}`;

      this.logs.update(prev => [...prev, 
        { sessionId: 'client', level: 'INFO', message: `🔹 ${namePart} | Build: ${buildPart} | Deploy: ${deployPart} | HB: ${hbPart}`, timestamp: new Date().toISOString() }
      ]);
    });

    this.logs.update(prev => [...prev, 
      { sessionId: 'client', level: 'SUCCESS', message: '------------------------------------------------------------------------', timestamp: new Date().toISOString() },
      { sessionId: 'client', level: 'SUCCESS', message: `🏁 ${totalTimeLabel}: ${totalDuration}`, timestamp: new Date().toISOString() },
      { sessionId: 'client', level: 'SUCCESS', message: '========================================================================', timestamp: new Date().toISOString() }
    ]);
    
    this.scrollToBottom();
  }

  getServiceName(id: string) { return this.services().find(s => s.id === id)?.name || 'Unknown'; }


  getLogClass(level: string): string {

    return `log-${level.toLowerCase()}`;
  }

  private startTimer() {
    this.startTime = Date.now();
    this.elapsedTime.set('00:00');
    if (this.timerInterval) clearInterval(this.timerInterval);

    this.timerInterval = setInterval(() => {
      const seconds = Math.floor((Date.now() - this.startTime) / 1000);
      const mins = Math.floor(seconds / 60);
      const secs = seconds % 60;
      this.elapsedTime.set(
        `${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`
      );
    }, 1000);
  }

  private stopTimer() {
    if (this.timerInterval) {
      clearInterval(this.timerInterval);
      this.timerInterval = null;
    }
  }

  private scrollToBottom() {
    setTimeout(() => {
      const el = document.getElementById('logContainer');
      if (el) el.scrollTop = el.scrollHeight;
    }, 50);
  }
}
