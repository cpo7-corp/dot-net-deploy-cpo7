import { Injectable, NgZone, signal } from '@angular/core';
import { ApiService } from './api.service';
import { DeployLogEntry, PagedResult, ProjectVersion } from '../models/api-models';
import { Observable, Subject } from 'rxjs';
import { HttpClient } from '@angular/common/http';

@Injectable({
  providedIn: 'root'
})
export class DeployService extends ApiService {
  // State signals that persist across page navigation
  deploying = signal<boolean>(false);
  logs = signal<DeployLogEntry[]>([]);
  deploymentProgress = signal<Record<string, { compiled: string; deployed: string; heartbeat: string; buildTime: string; buildStartTime?: number; deployTime: string; deployStartTime?: number; heartbeatTime: string; heartbeatStartTime?: number }>>({});
  elapsedTime = signal<string>('00:00');
  failedServiceIds = signal<string[]>([]);
  currentSessionId = signal<string | null>(null);
  isPaused = signal<boolean>(false);
  
  // Strategy options
  deployPull = signal<boolean>(true);
  deployBuild = signal<boolean>(true);
  deployTransfer = signal<boolean>(true);
  deployWaitAllBuilds = signal<boolean>(true);

  private timerInterval: any;
  private startTime: number = 0;

  constructor(private zone: NgZone, http: HttpClient) {
    super();
    this.http = http;
  }

  startDeployment(
    configs: { serviceId: string, branch: string }[], 
    environmentId: string | null, 
    forceClean: boolean, 
    pull: boolean, 
    build: boolean, 
    deploy: boolean,
    waitAllBuildsToDeploy: boolean = this.deployWaitAllBuilds()
  ) {
    this.deploying.set(true);
    this.logs.set([]);
    this.failedServiceIds.set([]);
    this.isPaused.set(false);
    
    // Initialize progress
    const initialProgress: any = {};
    configs.forEach(c => {
      initialProgress[c.serviceId] = { compiled: 'pending', deployed: 'pending', heartbeat: 'pending', buildTime: '', deployTime: '', heartbeatTime: '' };
    });
    this.deploymentProgress.set(initialProgress);
    
    this.startTimer();

    this.deploy(configs, environmentId, forceClean, pull, build, deploy, waitAllBuildsToDeploy).subscribe({
      next: (entry: DeployLogEntry) => {
        if (entry.level === 'SESSION_ID') {
          this.currentSessionId.set(entry.message);
          return;
        }

        this.logs.update(prev => [...prev, entry]);

        if (entry.serviceId) {
          this.updateServiceProgress(entry.serviceId, entry.message, entry.level);
        }

        if (entry.level === 'ERROR' && entry.serviceId) {
          this.failedServiceIds.update(fails => [...new Set([...fails, entry.serviceId!])]);
        }
      },
      complete: () => {
        this.deploying.set(false);
        this.currentSessionId.set(null);
        this.isPaused.set(false);
        this.stopTimer();
      },
      error: (err: any) => {
        this.deploying.set(false);
        this.currentSessionId.set(null);
        this.isPaused.set(false);
        this.stopTimer();
        this.logs.update(prev => [...prev, {
          sessionId: 'client',
          level: 'ERROR',
          message: 'Connection to server failed or dropped randomly.',
          created: new Date().toISOString()
        }]);
      }
    });
  }

  private updateServiceProgress(serviceId: string, message: string, level: string) {
    const progress = { ...this.deploymentProgress() };
    if (!progress[serviceId]) return;
    const row = { ...progress[serviceId] };

    // BUILD PHASE
    if (message.includes('🔨 [Prep] Building') || message.includes('📥 [Prep] Pulling')) {
      row.compiled = 'process';
      if (!row.buildStartTime) row.buildStartTime = Date.now();
    }
    if (message.includes('✅ [Prep] Prepared') || message.includes('⏭️ [Prep] Build output already exists') || (message.includes('✅') && message.includes('built'))) {
      row.compiled = 'success';
      if (row.buildStartTime && !row.buildTime) {
        row.buildTime = ((Date.now() - row.buildStartTime) / 1000).toFixed(1) + 's';
      }
    }
    if (message.includes('❌ Preparation failed') || message.includes('❌ Build failed')) {
      row.compiled = 'error';
      if (row.buildStartTime && !row.buildTime) {
        row.buildTime = ((Date.now() - row.buildStartTime) / 1000).toFixed(1) + 's';
      }
    }

    // DEPLOY PHASE
    if (message.includes('🚀 Uploading files') || message.includes('📂 Copying files')) {
      if (row.compiled === 'process' || row.compiled === 'pending') {
        row.compiled = 'success';
        if (row.buildStartTime && !row.buildTime) {
          row.buildTime = ((Date.now() - row.buildStartTime) / 1000).toFixed(1) + 's';
        }
      }
      row.deployed = 'process';
      if (!row.deployStartTime) row.deployStartTime = Date.now();
    }
    if (message.includes('✅ Files uploaded') || message.includes('✅ Files copied') || message.includes('🚀 Deploy complete') || message.includes('finished deployment successfully') || message.includes('Recording version')) {
      if (row.compiled === 'process' || row.compiled === 'pending') {
        row.compiled = 'success';
      }
      row.deployed = 'success';
      if (row.deployStartTime && !row.deployTime) {
        row.deployTime = ((Date.now() - row.deployStartTime) / 1000).toFixed(1) + 's';
      }
    }
    if (message.includes('❌ Failed to transfer') || message.includes('❌ Deploy failed')) {
      row.deployed = 'error';
      if (row.deployStartTime && !row.deployTime) {
        row.deployTime = ((Date.now() - row.deployStartTime) / 1000).toFixed(1) + 's';
      }
    }

    // HEARTBEAT PHASE
    if (message.includes('💓 Checking heartbeat')) {
      row.heartbeat = 'process';
      if (!row.heartbeatStartTime) row.heartbeatStartTime = Date.now();
    }
    if (message.includes('✅ Heartbeat OK')) {
      row.heartbeat = 'success';
      if (row.heartbeatStartTime && !row.heartbeatTime) {
        row.heartbeatTime = ((Date.now() - row.heartbeatStartTime) / 1000).toFixed(1) + 's';
      }
    }
    if (message.includes('⚠️ Heartbeat returned error') || message.includes('❌ Heartbeat failed')) {
      row.heartbeat = 'error';
      if (row.heartbeatStartTime && !row.heartbeatTime) {
        row.heartbeatTime = ((Date.now() - row.heartbeatStartTime) / 1000).toFixed(1) + 's';
      }
    }

    if (level === 'ERROR') {
      if (row.compiled === 'process') row.compiled = 'error';
      if (row.deployed === 'process') row.deployed = 'error';
      if (row.heartbeat === 'process') row.heartbeat = 'error';
    }

    progress[serviceId] = row;
    this.deploymentProgress.set(progress);
  }

  private startTimer() {
    this.startTime = Date.now();
    this.elapsedTime.set('00:00');
    if (this.timerInterval) clearInterval(this.timerInterval);
    this.timerInterval = setInterval(() => {
      const seconds = Math.floor((Date.now() - this.startTime) / 1000);
      const mins = Math.floor(seconds / 60);
      const secs = seconds % 60;
      this.elapsedTime.set(`${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`);
    }, 1000);
  }

  private stopTimer() {
    if (this.timerInterval) {
      clearInterval(this.timerInterval);
      this.timerInterval = null;
    }
  }

  deploy(services: { serviceId: string, branch?: string }[], environmentId?: string | null, forceClean: boolean = false, pull: boolean = true, build: boolean = true, deploy: boolean = true, waitAllBuildsToDeploy: boolean = true): Observable<DeployLogEntry> {
    return this.streamLogs(`${this.baseUrl}/deploy`, { services, environmentId, forceClean, pull, build, deploy, waitAllBuildsToDeploy });
  }

  serviceAction(serviceId: string, environmentId: string, action: string): Observable<DeployLogEntry> {
    return this.streamLogs(`${this.baseUrl}/deploy/service-action`, { serviceId, environmentId, action });
  }

  stop(sessionId: string): Observable<any> {
    return this.http!.post(`${this.baseUrl}/deploy/stop/${sessionId}`, {});
  }

  pause(sessionId: string): Observable<any> {
    return this.http!.post(`${this.baseUrl}/deploy/pause/${sessionId}`, {});
  }

  resume(sessionId: string): Observable<any> {
    return this.http!.post(`${this.baseUrl}/deploy/resume/${sessionId}`, {});
  }

  private streamLogs(url: string, body: any): Observable<DeployLogEntry> {
    const subject = new Subject<DeployLogEntry>();

    fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    }).then(async response => {
      if (!response.body) throw new Error('No body returned from server.');
      
      const reader = response.body.getReader();
      const decoder = new TextDecoder('utf-8');
      
      let buffer = '';
      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n\n');
        buffer = lines.pop() || '';

        for (const line of lines) {
          if (line.startsWith('data: ')) {
            const dataStr = line.replace('data: ', '').trim();
            if (dataStr) {
              const entry = JSON.parse(dataStr) as DeployLogEntry;
              this.zone.run(() => {
                subject.next(entry);
                if (entry.level === 'DONE') subject.complete();
              });
            }
          }
        }
      }
    }).catch(err => {
      this.zone.run(() => subject.error(err));
    });

    return subject.asObservable();
  }

  getSessions(count = 10): Observable<string[]> {
    return this.http!.get<string[]>(`${this.baseUrl}/Deploy/sessions?count=${count}`);
  }

  getSessionsPaged(skip = 0, limit = 20): Observable<SessionSummary[]> {
    return this.http!.get<SessionSummary[]>(`${this.baseUrl}/Deploy/sessions-paged?skip=${skip}&limit=${limit}`);
  }

  getLogs(sessionId: string): Observable<DeployLogEntry[]> {
    return this.http!.get<DeployLogEntry[]>(`${this.baseUrl}/Deploy/logs/${sessionId}`);
  }

  getCommits(repoUrl: string, branch: string = 'main', skip: number = 0, take: number = 20): Observable<PagedResult<ProjectVersion>> {
    return this.http!.get<PagedResult<ProjectVersion>>(
      `${this.baseUrl}/Git/commits?repoUrl=${encodeURIComponent(repoUrl)}&branch=${encodeURIComponent(branch)}&skip=${skip}&take=${take}`
    );
  }

  getHistory(serviceId: string, environmentId: string, limit: number = 20): Observable<any[]> {
    return this.http!.get<any[]>(`${this.baseUrl}/DeployHistory/${serviceId}/${environmentId}?limit=${limit}`);
  }
}

export interface SessionSummary {
  sessionId: string;
  created: string;
  hasErrors: boolean;
}
