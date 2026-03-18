import { Component, inject, OnInit, signal, effect } from '@angular/core';
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
  public deploySvc = inject(DeployService); // Public to access signals in HTML
  private settingsSvc = inject(SettingsService);
  private translate = inject(TranslateService);

  services = signal<ServiceStatus[]>([]);
  environments = signal<VpsSettings[]>([]);
  selectedEnvironmentId = signal<string | null>(null);
  selectedServiceIds: Set<string> = new Set();
  serviceBranches: Record<string, string> = {}; // Stores custom branch per service

  loading = signal<boolean>(true);

  // Read signals from service
  deploying = this.deploySvc.deploying;
  logs = this.deploySvc.logs;
  deploymentProgress = this.deploySvc.deploymentProgress;
  elapsedTime = this.deploySvc.elapsedTime;
  failedServiceIds = this.deploySvc.failedServiceIds;
  isPaused = this.deploySvc.isPaused;

  constructor() {
    // Scroll to bottom when logs change
    effect(() => {
      this.logs();
      this.scrollToBottom();
    });
  }

  ngOnInit() {
    this.servicesSvc.getAll().subscribe({
      next: (data) => {
        const sorted = data.sort((a, b) => a.name.localeCompare(b.name));
        this.services.set(sorted);
        this.updateDefaultBranches();
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

    const deploymentConfigs = configs || Array.from(this.selectedServiceIds).map(id => ({
      serviceId: id,
      branch: this.serviceBranches[id] || 'main'
    }));

    const pull = retryMode === 3 ? false : this.deploySvc.deployPull();
    const build = retryMode === 3 ? false : this.deploySvc.deployBuild();
    const deploy = retryMode === 3 ? true : this.deploySvc.deployTransfer();

    this.deploySvc.startDeployment(
      deploymentConfigs, 
      this.selectedEnvironmentId(), 
      forceClean, 
      pull, 
      build, 
      deploy
    );
  }

  retryFailed() {
    const failedIds = this.failedServiceIds();
    if (failedIds.length === 0) return;

    const configs = failedIds.map(id => ({
      serviceId: id,
      branch: this.serviceBranches[id] || 'main'
    }));

    this.startDeploy(configs, false, 3);
  }

  stopDeployment() {
    const sid = this.deploySvc.currentSessionId();
    if (sid) {
      this.deploySvc.stop(sid).subscribe();
    }
  }

  togglePause() {
    const sid = this.deploySvc.currentSessionId();
    if (!sid) return;

    if (this.isPaused()) {
      this.deploySvc.resume(sid).subscribe(() => this.deploySvc.isPaused.set(false));
    } else {
      this.deploySvc.pause(sid).subscribe(() => this.deploySvc.isPaused.set(true));
    }
  }

  getServiceName(id: string) { 
    return this.services().find(s => s.id === id)?.name || 'Unknown'; 
  }

  getLogClass(level: string): string {
    return `log-${level.toLowerCase()}`;
  }

  objectKeys(obj: any) { 
    return Object.keys(obj); 
  }

  private scrollToBottom() {
    setTimeout(() => {
      const el = document.getElementById('logContainer');
      if (el) el.scrollTop = el.scrollHeight;
    }, 50);
  }
}
