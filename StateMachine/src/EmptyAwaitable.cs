using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using RamjetAnvil.Coroutine;

namespace RamjetAnvil.StateMachine
{
    public class EmptyAwaitable : IAwaitable {
        public static readonly IAwaitable Default = new EmptyAwaitable();

        private EmptyAwaitable() {}

        public void Dispose() {}

        public bool IsDone {
            get { return true; }
        }
    }
}
