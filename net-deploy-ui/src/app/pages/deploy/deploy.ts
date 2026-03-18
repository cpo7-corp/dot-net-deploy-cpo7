import { Component, inject, OnInit, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { FormsModule } from '@angular/forms';
import { TranslateModule } from '@ngx-translate/core';
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

  services = signal<ServiceStatus[]>([]);
  environments = signal<VpsSettings[]>([]);
  selectedEnvironmentId = signal<string | null>(null);
  selectedServiceIds: Set<string> = new Set();
  serviceBranches: Record<string, string> = {}; // Stores custom branch per service

  loading = signal<boolean>(true);
  deploying = signal<boolean>(false);
  logs = signal<DeployLogEntry[]>([]);
  elapsedTime = signal<string>('00:00');
  failedServiceIds = signal<string[]>([]);
  cloneMultiple = signal<boolean>(false);
  skipBuild = signal<boolean>(false);
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
    });
  }

  onEnvironmentChange(id: string | null) {
    if (id) {
      localStorage.setItem('lastEnvironmentId', id);
    }
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

  startDeploy(configs?: { serviceId: string, branch: string }[], forceClean: boolean = true, cloneAllFirst: boolean | null = null, skipBuild: boolean | null = null) {
    if (!configs && this.selectedServiceIds.size === 0) return;

    this.deploying.set(true);
    this.logs.set([]);
    this.failedServiceIds.set([]);
    this.startTimer();

    const deploymentConfigs = configs || Array.from(this.selectedServiceIds).map(id => ({
      serviceId: id,
      branch: this.serviceBranches[id] || 'main'
    }));

    const shouldCloneAll = cloneAllFirst !== null ? cloneAllFirst : this.cloneMultiple();
    const shouldSkipBuild = skipBuild !== null ? skipBuild : this.skipBuild();

    this.deploySvc.deploy(deploymentConfigs, this.selectedEnvironmentId(), forceClean, shouldCloneAll, shouldSkipBuild).subscribe({
      next: (entry) => {
        this.logs.update(prev => [...prev, entry]);

        // Track failed services
        if (entry.level === 'ERROR' && entry.serviceId) {
          this.failedServiceIds.update(fails => [...new Set([...fails, entry.serviceId!])]);
        }

        this.scrollToBottom();
      },
      complete: () => {
        this.deploying.set(false);
        this.stopTimer();
        // Reload services to update status/last deployed date
        this.loading.set(true);
        this.servicesSvc.getAll().subscribe(data => {
          this.services.set(data.sort((a, b) => a.name.localeCompare(b.name)));
          this.loading.set(false);
        });
      },
      error: (err) => {
        this.deploying.set(false);
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

    this.startDeploy(configs, false, false, true);
  }

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
