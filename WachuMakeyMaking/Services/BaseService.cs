using WachuMakeyMaking.Events;

namespace WachuMakeyMaking.Services
{
    public class BaseService
    {
        private readonly EventSource updateStartEventSource = new();
        private readonly EventSource updateCompleteEventSource = new();
        private readonly EventSource initCompleteEventSource = new();

        public IEvent UpdateStartEvent => this.updateStartEventSource.Event();
        public IEvent UpdateCompleteEvent => this.updateCompleteEventSource.Event();
        public IEvent InitCompleteEvent => this.initCompleteEventSource.Event();

        protected void EmitUpdateStartEvent()
        {
            this.updateStartEventSource.Emit();
        }

        protected void EmitUpdateCompleteEvent()
        {
            this.updateCompleteEventSource.Emit();
        }

        protected void EmitInitCompleteEvent()
        {
            this.initCompleteEventSource.Emit();
        }
    }
}
